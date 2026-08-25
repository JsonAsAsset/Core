using System.Runtime.InteropServices;

using CUE4Parse.ACL;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Animation.ACL;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Readers;
using CUE4Parse.Utils;

using Core.Resources.Framework.Base;

using static CUE4Parse.UE4.Assets.Exports.Animation.AnimationCompressionFormat;
using static CUE4Parse.UE4.Assets.Exports.Animation.AnimationCompressionUtils;
using static CUE4Parse.UE4.Assets.Exports.Animation.AnimationKeyFormat;

using Microsoft.AspNetCore.Mvc;

using Serilog;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Animation                                                                                                 */
/*                                                                                                                                  */
/* The bone tracks of an animation sequence, decompressed. A cook keeps only the compressed stream and the keys an editor was given  */
/* are not in it, so they are read back out one bone at a time, in the pose the sequence stores rather than the pose it plays at:    */
/* nothing here is retargeted, and an additive sequence stays additive. What it is additive against is named for the other end to    */
/* set for itself.                                                                                                                  */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* One bone's keys, flattened: three floats a position, four a rotation, three a scale. A track
     * holds either one key, for a bone that never moves, or one per frame. */
    private sealed record AnimationTrack(string Bone, int BoneIndex, float[] Positions, float[] Rotations, float[] Scales);

    /* The keys of a sequence, per bone, and what the sequence plays them at.
     *
     * A sequence that is a difference from a frame of itself has nothing to be rebuilt against, so
     * base names the animation to use instead: what it was authored from, which the cook did not
     * keep. Ignored by any sequence that names a base of its own. */
    [HttpGet("export/animation")]
    public ActionResult GetAnimation(string? path, string? @base = null)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.animation.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1006
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        if (LoadExportOfType<UAnimSequence>(profile.Provider, path) is not { } animSequence)
        {
            return NotFoundResponse;
        }

        /* The bones the tracks are named by. A track says which bone of the skeleton it drives by
         * index, and an index is worth nothing at the other end, so the skeleton is read for the
         * names to send instead. */
        var skeleton = animSequence.Skeleton.Load<USkeleton>();

        if (skeleton is null)
        {
            return BadRequest(new
            {
                errorCode = "cloud.animation.no_skeleton",
                errorMessage = "The sequence's skeleton could not be loaded, so its tracks cannot be named",
                numericErrorCode = 1007
            });
        }

        var boneInfo = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        var trackMap = animSequence.GetTrackMap();

        if (BuildAnimationTracks(animSequence, skeleton, trackMap) is not { } tracks)
        {
            return BadRequest(new
            {
                errorCode = "cloud.animation.unsupported_codec",
                errorMessage = $"The sequence is compressed with {animSequence.CompressedDataStructure?.GetType().Name ?? "nothing this reads"}",
                numericErrorCode = 1008
            });
        }

        /* A track says which bone it drives by index into the skeleton, and everything from here on
         * is about bones rather than about the order the sequence happens to keep its tracks in */
        var byBone = ToBoneTracks(tracks, trackMap, boneInfo.Length);

        /* Stored as a difference, sent as the animation itself: the engine keeps an additive
         * sequence's own keys absolute and works the difference out again when it compresses, so
         * handing it the difference has it taken off twice. */
        /* Named by the sequence, handed in, or worked out here for the ones that name nothing usable */
        var baseOverride = ResolveBaseOverride(profile.Provider, @base) ?? ResolveKnownBase(profile, animSequence, path);

        if (IsAdditive(animSequence))
        {
            byBone = ToAbsolute(profile, animSequence, skeleton, byBone, depth: 0, baseOverride);
        }

        var trackEntries = new List<AnimationTrack>(byBone.Count);

        foreach (var (boneIndex, track) in byBone)
        {
            trackEntries.Add(new AnimationTrack(
                boneInfo[boneIndex].Name.Text,
                boneIndex,
                track.Positions,
                track.Rotations,
                track.Scales));
        }

        return new JsonResult(new
        {
            numFrames = animSequence.NumFrames,
            sequenceLength = animSequence.SequenceLength,
            rateScale = animSequence.RateScale,
            interpolation = animSequence.Interpolation.ToString(),

            /* An additive sequence holds the difference from a pose rather than the pose, and the
             * two are told apart by nothing in the keys themselves */
            additiveType = animSequence.AdditiveAnimType.ToString(),
            refPoseType = animSequence.RefPoseType.ToString(),
            refFrameIndex = animSequence.RefFrameIndex,
            refPoseSequence = animSequence.RefPoseSeq?.GetPathName(),
            retargetSource = animSequence.RetargetSource.Text,

            skeleton = skeleton.GetPathName(),

            /* Nothing was kept of what this one was a difference from: it names itself, so the
             * animation it was authored against is gone and can only be supplied from the other
             * end. Rebuilt against the skeleton's own pose until it is. */
            needsBase = IsAdditive(animSequence) && NeedsBase(animSequence) && baseOverride is null,
            baseUsed = baseOverride?.GetPathName(),

            tracks = trackEntries
        });
    }

    /* The keys of one bone, before they are named */
    private sealed record DecodedTrack(float[] Positions, float[] Rotations, float[] Scales);

    /* Decompresses every track the sequence has, keyed by track index. Null when the sequence is
     * compressed by something there is no reader for here. */
    private static Dictionary<int, DecodedTrack>? BuildAnimationTracks(UAnimSequence animSequence, USkeleton skeleton, FTrackToSkeletonMap[] trackMap)
    {
        switch (animSequence.CompressedDataStructure)
        {
            case FACLCompressedAnimData aclData:
                return ReadAclTracks(animSequence, skeleton, aclData, trackMap);

            case FUECompressedAnimData ueData:
                return ReadUeTracks(animSequence, ueData, trackMap);

            default:
                return null;
        }
    }

    /* ACL hands back a pose per track per sample rather than the keys it was given, which is what
     * the raw data an editor holds is anyway: one key a frame. */
    private static unsafe Dictionary<int, DecodedTrack> ReadAclTracks(UAnimSequence animSequence, USkeleton skeleton, FACLCompressedAnimData aclData, FTrackToSkeletonMap[] trackMap)
    {
        var compressedTracks = aclData.GetCompressedTracks();
        var header = compressedTracks.GetTracksHeader();

        var numSamples = (int) header.NumSamples;
        var numTracks = trackMap.Length;

        /* An additive sequence is a difference, and a difference of scale is measured from nothing
         * rather than from one */
        if (IsAdditive(animSequence)) compressedTracks.SetDefaultScale(0);

        var atomKeys = new FTransform[numTracks * numSamples];

        fixed (FTransform* refPosePtr = skeleton.ReferenceSkeleton.FinalRefBonePose)
        fixed (FTrackToSkeletonMap* trackMapPtr = trackMap)
        fixed (FTransform* atomKeysPtr = atomKeys)
        {
            nReadACLData(compressedTracks.Handle, refPosePtr, trackMapPtr, atomKeysPtr);
        }

        /* A looping clip is stored without its last sample, since that sample is the first one
         * again. Put back, so the sequence is the length it says it is. */
        var wraps = header.GetIsWrapOptimized() && numSamples > 0;
        var numKeys = wraps ? numSamples + 1 : numSamples;

        var tracks = new Dictionary<int, DecodedTrack>(numTracks);

        for (var trackIndex = 0; trackIndex < numTracks; trackIndex++)
        {
            var positions = new float[numKeys * 3];
            var rotations = new float[numKeys * 4];
            var scales = new float[numKeys * 3];

            for (var keyIndex = 0; keyIndex < numKeys; keyIndex++)
            {
                var sampleIndex = wraps && keyIndex == numSamples ? 0 : keyIndex;
                var atom = atomKeys[trackIndex * numSamples + sampleIndex];

                positions[keyIndex * 3 + 0] = (float) atom.Translation.X;
                positions[keyIndex * 3 + 1] = (float) atom.Translation.Y;
                positions[keyIndex * 3 + 2] = (float) atom.Translation.Z;

                rotations[keyIndex * 4 + 0] = (float) atom.Rotation.X;
                rotations[keyIndex * 4 + 1] = (float) atom.Rotation.Y;
                rotations[keyIndex * 4 + 2] = (float) atom.Rotation.Z;
                rotations[keyIndex * 4 + 3] = (float) atom.Rotation.W;

                scales[keyIndex * 3 + 0] = (float) atom.Scale3D.X;
                scales[keyIndex * 3 + 1] = (float) atom.Scale3D.Y;
                scales[keyIndex * 3 + 2] = (float) atom.Scale3D.Z;
            }

            tracks[trackIndex] = new DecodedTrack(positions, rotations, scales);
        }

        return tracks;
    }

    /* One track the way the stream holds it: as many keys as were kept, and the frames they sit on
     * when the codec stored keys for some frames and not others. */
    private sealed class RawTrack
    {
        public FVector[] Positions = [];
        public FQuat[] Rotations = [];
        public FVector[] Scales = [];

        public float[] PositionTimes = [];
        public float[] RotationTimes = [];
        public float[] ScaleTimes = [];
    }

    /* The engine's own codecs, which keep the keys rather than a stream to sample from: one lot of
     * offsets per track into a single byte stream, and a format per channel saying how wide each
     * key was packed. A channel with no offset at all was left out of the cook, and the bone keeps
     * whatever the pose it plays against gives it, which is said here by sending no keys for it. */
    private static Dictionary<int, DecodedTrack> ReadUeTracks(UAnimSequence animSequence, FUECompressedAnimData ueData, FTrackToSkeletonMap[] trackMap)
    {
        var numFrames = animSequence.NumFrames;

        using var reader = new FByteArchive("CompressedByteStream", ueData.CompressedByteStream);

        var tracks = new Dictionary<int, DecodedTrack>(trackMap.Length);

        for (var trackIndex = 0; trackIndex < trackMap.Length; trackIndex++)
        {
            var raw = new RawTrack();

            if (ueData.KeyEncodingFormat == AKF_PerTrackCompression)
            {
                ReadPerTrack(reader, ueData, raw, trackIndex, numFrames);
            }
            else
            {
                ReadKeyLerp(reader, ueData, raw, trackIndex, numFrames, ueData.KeyEncodingFormat == AKF_VariableKeyLerp);
            }

            tracks[trackIndex] = new DecodedTrack(
                SampleVectors(raw.Positions, raw.PositionTimes, numFrames),
                SampleQuats(raw.Rotations, raw.RotationTimes, numFrames),
                SampleVectors(raw.Scales, raw.ScaleTimes, numFrames));
        }

        return tracks;
    }

    /* Per track compression: every channel names its own offset, and a header at that offset says
     * the format, which components were kept and how many keys follow. */
    private static void ReadPerTrack(FArchive reader, FUECompressedAnimData ueData, RawTrack track, int trackIndex, int numFrames)
    {
        var transOffset = ueData.CompressedTrackOffsets[trackIndex * 2];
        var rotOffset = ueData.CompressedTrackOffsets[trackIndex * 2 + 1];
        var scaleOffset = ueData.CompressedScaleOffsets.IsValid() ? ueData.CompressedScaleOffsets.OffsetData[trackIndex] : -1;

        if (transOffset != -1)
        {
            reader.Position = transOffset;
            ReadPerTrackVectors(reader, numFrames, out track.Positions, out track.PositionTimes);
        }

        if (rotOffset != -1)
        {
            reader.Position = rotOffset;
            ReadPerTrackQuats(reader, numFrames, out track.Rotations, out track.RotationTimes);
        }

        if (scaleOffset != -1)
        {
            reader.Position = scaleOffset;
            ReadPerTrackVectors(reader, numFrames, out track.Scales, out track.ScaleTimes);
        }
    }

    private static void ReadPerTrackVectors(FArchive reader, int numFrames, out FVector[] keys, out float[] times)
    {
        var packedInfo = reader.Read<uint>();
        var keyFormat = (AnimationCompressionFormat) (packedInfo >> 28);
        var componentMask = (int) ((packedInfo >> 24) & 0xF);
        var numKeys = (int) (packedInfo & 0xFFFFFF);
        var hasTimeTracks = (componentMask & 8) != 0;

        var mins = FVector.ZeroVector;
        var ranges = FVector.ZeroVector;

        if (keyFormat == ACF_IntervalFixed32NoW)
        {
            if ((componentMask & 1) != 0) { mins.X = reader.Read<float>(); ranges.X = reader.Read<float>(); }
            if ((componentMask & 2) != 0) { mins.Y = reader.Read<float>(); ranges.Y = reader.Read<float>(); }
            if ((componentMask & 4) != 0) { mins.Z = reader.Read<float>(); ranges.Z = reader.Read<float>(); }
        }

        keys = new FVector[numKeys];

        for (var keyIndex = 0; keyIndex < numKeys; keyIndex++)
        {
            keys[keyIndex] = keyFormat switch
            {
                /* Float96 keeps whichever components the mask names, and the whole vector when it
                 * names none of them */
                ACF_None or ACF_Float96NoW => (componentMask & 7) != 0
                    ? new FVector(
                        (componentMask & 1) != 0 ? reader.Read<float>() : 0,
                        (componentMask & 2) != 0 ? reader.Read<float>() : 0,
                        (componentMask & 4) != 0 ? reader.Read<float>() : 0)
                    : reader.Read<FVector>(),
                ACF_IntervalFixed32NoW => reader.ReadVectorIntervalFixed32(mins, ranges),
                ACF_Fixed48NoW => new FVector(
                    (componentMask & 1) != 0 ? DecodeFixed48_PerTrackComponent(reader.Read<ushort>(), 7) : 0,
                    (componentMask & 2) != 0 ? DecodeFixed48_PerTrackComponent(reader.Read<ushort>(), 7) : 0,
                    (componentMask & 4) != 0 ? DecodeFixed48_PerTrackComponent(reader.Read<ushort>(), 7) : 0),
                ACF_Identity => FVector.ZeroVector,
                _ => throw new ParserException(reader, "Unknown vector compression method: " + (int) keyFormat)
            };
        }

        reader.Position = reader.Position.Align(4);

        times = hasTimeTracks ? ReadTimeArray(reader, numKeys, numFrames) : [];
    }

    private static void ReadPerTrackQuats(FArchive reader, int numFrames, out FQuat[] keys, out float[] times)
    {
        var packedInfo = reader.Read<uint>();
        var keyFormat = (AnimationCompressionFormat) (packedInfo >> 28);
        var componentMask = (int) ((packedInfo >> 24) & 0xF);
        var numKeys = (int) (packedInfo & 0xFFFFFF);
        var hasTimeTracks = (componentMask & 8) != 0;

        var mins = FVector.ZeroVector;
        var ranges = FVector.ZeroVector;

        if (keyFormat == ACF_IntervalFixed32NoW)
        {
            if ((componentMask & 1) != 0) { mins.X = reader.Read<float>(); ranges.X = reader.Read<float>(); }
            if ((componentMask & 2) != 0) { mins.Y = reader.Read<float>(); ranges.Y = reader.Read<float>(); }
            if ((componentMask & 4) != 0) { mins.Z = reader.Read<float>(); ranges.Z = reader.Read<float>(); }
        }

        keys = new FQuat[numKeys];

        for (var keyIndex = 0; keyIndex < numKeys; keyIndex++)
        {
            keys[keyIndex] = keyFormat switch
            {
                ACF_None or ACF_Float96NoW => reader.ReadQuatFloat96NoW(),
                ACF_Fixed48NoW => reader.ReadQuatFixed48NoW(componentMask),
                ACF_Fixed32NoW => reader.ReadQuatFixed32NoW(),
                ACF_IntervalFixed32NoW => reader.ReadQuatIntervalFixed32NoW(mins, ranges),
                ACF_Float32NoW => reader.ReadQuatFloat32NoW(),
                ACF_Identity => FQuat.Identity,
                _ => throw new ParserException(reader, "Unknown rotation compression method: " + (int) keyFormat)
            };
        }

        reader.Position = reader.Position.Align(4);

        times = hasTimeTracks ? ReadTimeArray(reader, numKeys, numFrames) : [];
    }

    /* Key lerp compression: the offsets and key counts sit in the table rather than in a header,
     * and every key of a channel is packed the same way. */
    private static void ReadKeyLerp(FArchive reader, FUECompressedAnimData ueData, RawTrack track, int trackIndex, int numFrames, bool hasTimeTracks)
    {
        var transOffset = ueData.CompressedTrackOffsets[trackIndex * 4];
        var transKeys = ueData.CompressedTrackOffsets[trackIndex * 4 + 1];
        var rotOffset = ueData.CompressedTrackOffsets[trackIndex * 4 + 2];
        var rotKeys = ueData.CompressedTrackOffsets[trackIndex * 4 + 3];

        var scaleOffset = 0;
        var scaleKeys = 0;

        if (ueData.CompressedScaleOffsets.IsValid())
        {
            scaleOffset = ueData.CompressedScaleOffsets.OffsetData[trackIndex * 2];
            scaleKeys = ueData.CompressedScaleOffsets.OffsetData[trackIndex * 2 + 1];
        }

        if (transKeys > 0)
        {
            track.Positions = ReadVectorKeys(reader, transOffset, transKeys, ueData.TranslationCompressionFormat);

            reader.Position = reader.Position.Align(4);

            if (hasTimeTracks) track.PositionTimes = ReadTimeArray(reader, transKeys, numFrames);
        }

        if (scaleKeys > 0 && scaleOffset > 0)
        {
            track.Scales = ReadVectorKeys(reader, scaleOffset, scaleKeys, ueData.ScaleCompressionFormat);

            reader.Position = reader.Position.Align(4);

            if (hasTimeTracks) track.ScaleTimes = ReadTimeArray(reader, scaleKeys, numFrames);
        }

        if (rotKeys > 0)
        {
            reader.Position = rotOffset;

            /* A single key is written out whole, whatever the track's format says */
            var format = rotKeys == 1 ? ACF_Float96NoW : ueData.RotationCompressionFormat;

            var mins = FVector.ZeroVector;
            var ranges = FVector.ZeroVector;

            if (format == ACF_IntervalFixed32NoW)
            {
                mins = reader.Read<FVector>();
                ranges = reader.Read<FVector>();
            }

            track.Rotations = new FQuat[rotKeys];

            for (var keyIndex = 0; keyIndex < rotKeys; keyIndex++)
            {
                track.Rotations[keyIndex] = format switch
                {
                    ACF_None => reader.Read<FQuat>(),
                    ACF_Float96NoW => reader.ReadQuatFloat96NoW(),
                    ACF_Fixed48NoW => reader.ReadQuatFixed48NoW(),
                    ACF_Fixed32NoW => reader.ReadQuatFixed32NoW(),
                    ACF_IntervalFixed32NoW => reader.ReadQuatIntervalFixed32NoW(mins, ranges),
                    ACF_Float32NoW => reader.ReadQuatFloat32NoW(),
                    ACF_Identity => FQuat.Identity,
                    _ => throw new ParserException(reader, "Unknown rotation compression method: " + (int) format)
                };
            }

            reader.Position = reader.Position.Align(4);

            if (hasTimeTracks) track.RotationTimes = ReadTimeArray(reader, rotKeys, numFrames);
        }
    }

    private static FVector[] ReadVectorKeys(FArchive reader, long offset, int numKeys, AnimationCompressionFormat format)
    {
        reader.Position = offset;

        /* A single key is written out whole, whatever the track's format says */
        if (numKeys == 1) format = ACF_None;

        var mins = FVector.ZeroVector;
        var ranges = FVector.ZeroVector;

        if (format == ACF_IntervalFixed32NoW)
        {
            mins = reader.Read<FVector>();
            ranges = reader.Read<FVector>();
        }

        var keys = new FVector[numKeys];

        for (var keyIndex = 0; keyIndex < numKeys; keyIndex++)
        {
            keys[keyIndex] = format switch
            {
                ACF_None or ACF_Float96NoW => reader.Read<FVector>(),
                ACF_IntervalFixed32NoW => reader.ReadVectorIntervalFixed32(mins, ranges),
                ACF_Fixed48NoW => reader.ReadVectorFixed48(),
                ACF_Identity => FVector.ZeroVector,
                _ => throw new ParserException(reader, "Unknown vector compression method: " + (int) format)
            };
        }

        return keys;
    }

    /* Which frame each key sits on, one byte a key up to 255 frames and two after that */
    private static float[] ReadTimeArray(FArchive reader, int numKeys, int numFrames)
    {
        var times = new float[numKeys];

        if (numKeys <= 1) return times;

        for (var keyIndex = 0; keyIndex < numKeys; keyIndex++)
        {
            times[keyIndex] = numFrames < 256 ? reader.Read<byte>() : reader.Read<ushort>();
        }

        reader.Position = reader.Position.Align(4);

        return times;
    }

    /* One key a frame, which is the shape the other end writes into the asset.
     *
     * A channel the cook kept a key a frame for comes back as it is, a channel that never changes
     * stays the single key it was kept as, and a channel the cook thinned out is read again at the
     * frames that were dropped. */
    private static float[] SampleVectors(FVector[] keys, float[] times, int numFrames)
    {
        if (keys.Length == 0) return [];

        if (keys.Length == 1)
        {
            return [(float) keys[0].X, (float) keys[0].Y, (float) keys[0].Z];
        }

        var sampled = new float[numFrames * 3];

        var direct = times.Length == 0 && keys.Length == numFrames;

        for (var frame = 0; frame < numFrames; frame++)
        {
            FVector value;

            if (direct)
            {
                value = keys[frame];
            }
            else
            {
                FindKeys(times, keys.Length, frame, numFrames, out var previous, out var next, out var alpha);

                value = keys[previous] + (keys[next] - keys[previous]) * alpha;
            }

            sampled[frame * 3 + 0] = (float) value.X;
            sampled[frame * 3 + 1] = (float) value.Y;
            sampled[frame * 3 + 2] = (float) value.Z;
        }

        return sampled;
    }

    private static float[] SampleQuats(FQuat[] keys, float[] times, int numFrames)
    {
        if (keys.Length == 0) return [];

        if (keys.Length == 1)
        {
            var only = Unit(keys[0]);

            return [(float) only.X, (float) only.Y, (float) only.Z, (float) only.W];
        }

        var sampled = new float[numFrames * 4];

        var direct = times.Length == 0 && keys.Length == numFrames;

        for (var frame = 0; frame < numFrames; frame++)
        {
            FQuat value;

            if (direct)
            {
                value = keys[frame];
            }
            else
            {
                FindKeys(times, keys.Length, frame, numFrames, out var previous, out var next, out var alpha);

                value = alpha > 0.0f ? FQuat.Slerp(keys[previous], keys[next], alpha) : keys[previous];
            }

            value = Unit(value);

            sampled[frame * 4 + 0] = (float) value.X;
            sampled[frame * 4 + 1] = (float) value.Y;
            sampled[frame * 4 + 2] = (float) value.Z;
            sampled[frame * 4 + 3] = (float) value.W;
        }

        return sampled;
    }

    /* The two keys a frame falls between, and how far between them it is. Keys sit on the frames
     * the time array names, or evenly across the sequence when the codec kept no times. */
    private static void FindKeys(float[] times, int keyCount, float frame, int numFrames, out int previous, out int next, out float alpha)
    {
        if (times.Length == 0)
        {
            var position = numFrames > 0 ? frame / numFrames * keyCount : 0.0f;

            previous = (int) MathF.Floor(position);
            alpha = position - previous;
            next = previous + 1;

            if (next >= keyCount)
            {
                next = keyCount - 1;
                previous = Math.Min(previous, next);
                alpha = 0.0f;
            }

            return;
        }

        previous = 0;

        for (var keyIndex = 0; keyIndex < times.Length; keyIndex++)
        {
            if (times[keyIndex] > frame) break;

            previous = keyIndex;
        }

        next = previous + 1;

        if (next >= times.Length)
        {
            next = times.Length - 1;
            previous = next;
            alpha = 0.0f;

            return;
        }

        var span = times[next] - times[previous];

        alpha = span > 0.0f ? (frame - times[previous]) / span : 0.0f;
    }

    /* The tracks of a sequence, by the bone each one drives */
    private static Dictionary<int, DecodedTrack> ToBoneTracks(Dictionary<int, DecodedTrack> tracks, FTrackToSkeletonMap[] trackMap, int boneCount)
    {
        var byBone = new Dictionary<int, DecodedTrack>(tracks.Count);

        foreach (var (trackIndex, track) in tracks)
        {
            var boneIndex = trackIndex < trackMap.Length ? trackMap[trackIndex].BoneTreeIndex : -1;
            if (boneIndex < 0 || boneIndex >= boneCount) continue;

            byBone[boneIndex] = track;
        }

        return byBone;
    }

    /* The animation an additive sequence is a difference from.
     *
     * What the difference is measured against is named by the sequence: the skeleton's own pose, a
     * frame of another sequence, or another sequence played across the same length as this one.
     * Rebuilt here so the keys that go over are the animation rather than the difference, which is
     * the form the engine keeps its own in.
     *
     * A difference only names the bones it changes. Every bone the base animation moves is in the
     * result too, moving the way the base moves it, or the sequence would come out holding still
     * everywhere the difference had nothing to say. */
    private static Dictionary<int, DecodedTrack> ToAbsolute(BaseProfile profile, UAnimSequence animSequence, USkeleton skeleton, Dictionary<int, DecodedTrack> deltas, int depth, UAnimSequence? baseOverride = null)
    {
        var numFrames = Math.Max(1, animSequence.NumFrames);
        var refBonePose = skeleton.ReferenceSkeleton.FinalRefBonePose;

        var baseByBone = new Dictionary<int, DecodedTrack>();
        var baseFrames = 1;
        var scaledBase = false;

        /* What it names, unless it names itself and something else was handed in for it */
        var namedBase = animSequence.RefPoseSeq?.Load<UAnimSequence>();

        if (namedBase is null || ReferenceEquals(namedBase, animSequence) || namedBase.Name == animSequence.Name)
        {
            namedBase = baseOverride;
        }

        switch (animSequence.RefPoseType)
        {
            case EAdditiveBasePoseType.ABPT_AnimFrame:
            case EAdditiveBasePoseType.ABPT_AnimScaled:
            case EAdditiveBasePoseType.ABPT_LocalAnimFrame:
            {
                /* Pointing at itself with nothing handed in, there is nothing to load: the frame it
                 * names is one of its own, and its own keys are the difference rather than the
                 * animation. Left with no base, every bone is built against the bind pose below. */
                if (namedBase is { } refSeq)
                {
                    var refMap = refSeq.GetTrackMap();

                    if (BuildAnimationTracks(refSeq, skeleton, refMap) is { } refTracks)
                    {
                        baseByBone = ToBoneTracks(refTracks, refMap, refBonePose.Length);

                        /* The base is read as the animation it is, not as what it is a difference
                         * from: the engine takes it with GetBonePose, which is the sequence's own
                         * keys and nothing else. A base that is itself additive is therefore built
                         * back up the same way this one is, before it is any use as a base -- and
                         * what to build it over is worked out the same way too, since a base that
                         * names nothing usable is no rarer than any other sequence that doesn't. */
                        if (IsAdditive(refSeq) && depth < MaxAdditiveDepth)
                        {
                            baseByBone = ToAbsolute(profile, refSeq, skeleton, baseByBone, depth + 1,
                                ResolveKnownBase(profile, refSeq, PathOf(refSeq)));
                        }

                        baseFrames = Math.Max(1, refSeq.NumFrames);
                        /* A base handed in for a sequence that named itself is the animation it
                         * was authored over, so it is read frame for frame the way a scaled base
                         * is rather than pinned to the one frame the sequence names. */
                        scaledBase = animSequence.RefPoseType == EAdditiveBasePoseType.ABPT_AnimScaled
                            || ReferenceEquals(refSeq, baseOverride);
                    }
                }

                break;
            }
        }

        /* An aim offset turns its bones in the space the mesh is drawn in rather than in the space
         * each bone sits in, so the whole hierarchy is needed to take the difference back off */
        if (animSequence.AdditiveAnimType == EAdditiveAnimationType.AAT_RotationOffsetMeshSpace)
        {
            return ToAbsoluteMeshSpace(animSequence, skeleton, deltas, baseByBone, baseFrames, scaledBase);
        }

        var bones = new HashSet<int>(deltas.Keys);
        bones.UnionWith(baseByBone.Keys);

        var absolute = new Dictionary<int, DecodedTrack>(bones.Count);

        foreach (var boneIndex in bones)
        {
            if (boneIndex < 0 || boneIndex >= refBonePose.Length) continue;

            var bindPose = refBonePose[boneIndex];

            deltas.TryGetValue(boneIndex, out var delta);
            baseByBone.TryGetValue(boneIndex, out var baseTrack);

            var positions = new float[numFrames * 3];
            var rotations = new float[numFrames * 4];
            var scales = new float[numFrames * 3];

            for (var frame = 0; frame < numFrames; frame++)
            {
                var basePose = bindPose;

                if (baseTrack is not null)
                {
                    /* Where in the base to read, worked out the way the engine works it out: a
                     * scaled base runs its own length against this one, and a frame base sits on
                     * the one frame the sequence names. */
                    var basePosition = BasePosition(animSequence, frame, numFrames, baseFrames, scaledBase);

                    basePose = ReadKeyAt(baseTrack, basePosition, bindPose);
                }

                var deltaPose = delta is not null ? ReadKey(delta, frame, AdditiveIdentity) : AdditiveIdentity;

                /* The difference the engine takes: rotation off the base's, translation away from
                 * it, and scale as how much of it there is either way of one */
                var rotation = Unit(deltaPose.Rotation * basePose.Rotation);

                var translation = deltaPose.Translation + basePose.Translation;

                var scale = new FVector(
                    (deltaPose.Scale3D.X + 1.0f) * basePose.Scale3D.X,
                    (deltaPose.Scale3D.Y + 1.0f) * basePose.Scale3D.Y,
                    (deltaPose.Scale3D.Z + 1.0f) * basePose.Scale3D.Z);

                positions[frame * 3 + 0] = (float) translation.X;
                positions[frame * 3 + 1] = (float) translation.Y;
                positions[frame * 3 + 2] = (float) translation.Z;

                rotations[frame * 4 + 0] = (float) rotation.X;
                rotations[frame * 4 + 1] = (float) rotation.Y;
                rotations[frame * 4 + 2] = (float) rotation.Z;
                rotations[frame * 4 + 3] = (float) rotation.W;

                scales[frame * 3 + 0] = (float) scale.X;
                scales[frame * 3 + 1] = (float) scale.Y;
                scales[frame * 3 + 2] = (float) scale.Z;
            }

            absolute[boneIndex] = new DecodedTrack(positions, rotations, scales);
        }

        return absolute;
    }

    /* The same rebuild, for a difference measured in mesh space.
     *
     * An aim offset stores how far a bone is turned from the base as seen from the mesh, not from
     * the bone's parent, so the difference cannot be put back one bone at a time: every rotation
     * has to be walked down the hierarchy into mesh space, turned there, and walked back out. A
     * bone the difference says nothing about still comes out turned differently in its own space
     * when something above it moved, which is why the result covers what hangs off a turned bone
     * as well as what the difference and the base name between them. */
    private static Dictionary<int, DecodedTrack> ToAbsoluteMeshSpace(UAnimSequence animSequence, USkeleton skeleton, Dictionary<int, DecodedTrack> deltas, Dictionary<int, DecodedTrack> baseByBone, int baseFrames, bool scaledBase)
    {
        var numFrames = Math.Max(1, animSequence.NumFrames);

        var boneInfo = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        var refBonePose = skeleton.ReferenceSkeleton.FinalRefBonePose;
        var boneCount = Math.Min(boneInfo.Length, refBonePose.Length);

        /* What the difference names, what the base names, and everything hanging off a bone the
         * difference turns */
        var affected = new HashSet<int>(deltas.Keys);
        affected.UnionWith(baseByBone.Keys);

        for (var boneIndex = 0; boneIndex < boneCount; boneIndex++)
        {
            for (var ancestor = boneInfo[boneIndex].ParentIndex; ancestor >= 0 && ancestor < boneCount; ancestor = boneInfo[ancestor].ParentIndex)
            {
                if (!deltas.ContainsKey(ancestor)) continue;

                affected.Add(boneIndex);

                break;
            }
        }

        affected.RemoveWhere(boneIndex => boneIndex < 0 || boneIndex >= boneCount);

        var positions = new Dictionary<int, float[]>(affected.Count);
        var rotations = new Dictionary<int, float[]>(affected.Count);
        var scales = new Dictionary<int, float[]>(affected.Count);

        foreach (var boneIndex in affected)
        {
            positions[boneIndex] = new float[numFrames * 3];
            rotations[boneIndex] = new float[numFrames * 4];
            scales[boneIndex] = new float[numFrames * 3];
        }

        var basePose = new FTransform[boneCount];
        var meshRotation = new FQuat[boneCount];
        var turnedRotation = new FQuat[boneCount];

        for (var frame = 0; frame < numFrames; frame++)
        {
            /* The base, bone by bone, in the space each bone sits in */
            for (var boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                var bindPose = refBonePose[boneIndex];

                basePose[boneIndex] = baseByBone.TryGetValue(boneIndex, out var baseTrack)
                    ? ReadKeyAt(baseTrack, BasePosition(animSequence, frame, numFrames, baseFrames, scaledBase), bindPose)
                    : bindPose;
            }

            /* Down the hierarchy into mesh space: a bone's rotation there is its parent's and its
             * own together, and the root's is already its own */
            for (var boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                var parentIndex = boneInfo[boneIndex].ParentIndex;

                meshRotation[boneIndex] = parentIndex >= 0 && parentIndex < boneCount
                    ? meshRotation[parentIndex] * basePose[boneIndex].Rotation
                    : basePose[boneIndex].Rotation;

                turnedRotation[boneIndex] = meshRotation[boneIndex];
            }

            /* The turn the difference asks for, taken in that same space */
            foreach (var (boneIndex, delta) in deltas)
            {
                if (boneIndex < 0 || boneIndex >= boneCount) continue;

                var deltaPose = ReadKey(delta, frame, AdditiveIdentity);

                turnedRotation[boneIndex] = Unit(deltaPose.Rotation * meshRotation[boneIndex]);
            }

            /* And back out, deepest first, so a bone is read against the parent it is drawn under
             * rather than against the one it was */
            for (var boneIndex = boneCount - 1; boneIndex >= 0; boneIndex--)
            {
                if (!affected.Contains(boneIndex)) continue;

                var parentIndex = boneInfo[boneIndex].ParentIndex;

                var local = parentIndex >= 0 && parentIndex < boneCount
                    ? Unit(FQuat.Conjugate(turnedRotation[parentIndex]) * turnedRotation[boneIndex])
                    : turnedRotation[boneIndex];

                /* Where a bone sits and how big it is are still measured against its parent, the
                 * way they are for every other kind of difference */
                var deltaPose = deltas.TryGetValue(boneIndex, out var delta)
                    ? ReadKey(delta, frame, AdditiveIdentity)
                    : AdditiveIdentity;

                var translation = deltaPose.Translation + basePose[boneIndex].Translation;

                var scale = new FVector(
                    (deltaPose.Scale3D.X + 1.0f) * basePose[boneIndex].Scale3D.X,
                    (deltaPose.Scale3D.Y + 1.0f) * basePose[boneIndex].Scale3D.Y,
                    (deltaPose.Scale3D.Z + 1.0f) * basePose[boneIndex].Scale3D.Z);

                var boneRotations = rotations[boneIndex];
                var bonePositions = positions[boneIndex];
                var boneScales = scales[boneIndex];

                boneRotations[frame * 4 + 0] = (float) local.X;
                boneRotations[frame * 4 + 1] = (float) local.Y;
                boneRotations[frame * 4 + 2] = (float) local.Z;
                boneRotations[frame * 4 + 3] = (float) local.W;

                bonePositions[frame * 3 + 0] = (float) translation.X;
                bonePositions[frame * 3 + 1] = (float) translation.Y;
                bonePositions[frame * 3 + 2] = (float) translation.Z;

                boneScales[frame * 3 + 0] = (float) scale.X;
                boneScales[frame * 3 + 1] = (float) scale.Y;
                boneScales[frame * 3 + 2] = (float) scale.Z;
            }
        }

        var absolute = new Dictionary<int, DecodedTrack>(affected.Count);

        foreach (var boneIndex in affected)
        {
            absolute[boneIndex] = new DecodedTrack(positions[boneIndex], rotations[boneIndex], scales[boneIndex]);
        }

        return absolute;
    }

    /* Where in the base to read for a given frame of the sequence sitting on it */
    private static float BasePosition(UAnimSequence animSequence, int frame, int numFrames, int baseFrames, bool scaledBase)
    {
        var fraction = scaledBase
            ? (numFrames > 1 ? (float) frame / (numFrames - 1) : 0.0f)
            : (baseFrames > 0 ? Math.Clamp((float) animSequence.RefFrameIndex / baseFrames, 0.0f, 1.0f) : 0.0f);

        return fraction * (baseFrames - 1);
    }

    /* A track read between its keys, for a base animation whose frames don't line up with the ones
     * being written against it */
    private static FTransform ReadKeyAt(DecodedTrack track, float framePosition, FTransform fallback)
    {
        var previousFrame = (int) MathF.Floor(framePosition);
        var alpha = framePosition - previousFrame;

        var previous = ReadKey(track, previousFrame, fallback);

        if (alpha <= 0.0f) return previous;

        var next = ReadKey(track, previousFrame + 1, fallback);

        var rotation = Unit(FQuat.Slerp(previous.Rotation, next.Rotation, alpha));

        return new FTransform(
            rotation,
            previous.Translation + (next.Translation - previous.Translation) * alpha,
            previous.Scale3D + (next.Scale3D - previous.Scale3D) * alpha);
    }

    /* Exactly unit length, rather than nearly: the engine's own normalize takes an inverse square
     * root by estimate, which leaves a rotation key a fifth of a percent short every time it is
     * used, and these are multiplied together. */
    private static FQuat Unit(FQuat rotation)
    {
        var lengthSquared = rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z + rotation.W * rotation.W;

        if (lengthSquared <= 0.0f) return FQuat.Identity;

        var scale = 1.0f / MathF.Sqrt((float) lengthSquared);

        return new FQuat((float) rotation.X * scale, (float) rotation.Y * scale, (float) rotation.Z * scale, (float) rotation.W * scale);
    }

    /* Whether the sequence says what it is a difference from.
     *
     * Three ways it doesn't: it names a frame of itself, it names an animation that isn't there, or
     * it names nothing at all -- an aim offset pose keeps the kind of difference it is and drops
     * the animation it was taken from. All three leave the difference with nothing to be put back
     * over, so all three are worth asking about at the other end. */
    private static bool NeedsBase(UAnimSequence sequence)
    {
        if (sequence.RefPoseType == EAdditiveBasePoseType.ABPT_LocalAnimFrame) return true;

        if (sequence.RefPoseSeq?.Load<UAnimSequence>() is not { } refSeq) return true;

        return ReferenceEquals(refSeq, sequence) || refSeq.Name == sequence.Name;
    }

    /* What a sequence that names nothing usable was built over, worked out rather than read.
     *
     * The pairs named here first, then the ones the game's own naming gives away. Used for the
     * sequence being asked for and for any it leans on, since a difference is no easier to put back
     * for being one somebody else's difference is measured against. */
    private static UAnimSequence? ResolveKnownBase(BaseProfile profile, UAnimSequence sequence, string path)
    {
        if (!IsAdditive(sequence) || !NeedsBase(sequence)) return null;

        var knownBases = FindAdditiveBasePoses(sequence.Name);

        foreach (var knownBase in knownBases)
        {
            if (ResolveBaseOverride(profile.Provider, knownBase) is { } known) return known;
        }

        if (knownBases.Length > 0)
        {
            Log.Warning("[Core.Cloud]: \"{Sequence}\" is built over {Bases}, none of which this game has got",
                sequence.Name, string.Join(", ", knownBases));
        }

        return ResolveBaseOverride(profile.Provider, FindDerivedBasePose(profile, sequence.Name, path));
    }

    /* Where the game keeps a loaded animation, for working out what sits beside it */
    private static string PathOf(UAnimSequence sequence) => sequence.Owner?.Name ?? string.Empty;

    /* The animation handed in to stand in for a base that was never kept. Null when none was named,
     * or when what was named is not an animation this reads. */
    private static UAnimSequence? ResolveBaseOverride(BaseProvider provider, string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath)) return null;

        return LoadExportOfType<UAnimSequence>(provider, basePath.SubstringBefore('.'));
    }

    /* Whether the sequence is a difference, by the engine's rule rather than by the reader's.
     *
     * The reader calls a sequence that is a difference from a frame of itself no difference at all,
     * on the grounds that it points at itself. The engine takes it at its word and reads the frame
     * out of its own keys, so it is a difference here too: one measured against a pose this end has
     * to pick, since the animation the cook subtracted was never written down. The bind pose is
     * what it is measured against below, which is exact wherever the first frame is no difference
     * at all -- which is what a sequence built against itself always stores. */
    private static bool IsAdditive(UAnimSequence sequence) => sequence.AdditiveAnimType != EAdditiveAnimationType.AAT_None
        && sequence.RefPoseType != EAdditiveBasePoseType.ABPT_None;

    /* A difference against a difference is ordinary enough; a chain of them going nowhere is not */
    private const int MaxAdditiveDepth = 4;

    /* No difference at all: no turn, no move, and a scale of one measured as none either way */
    private static readonly FTransform AdditiveIdentity = new(FQuat.Identity, FVector.ZeroVector, FVector.ZeroVector);

    /* One key of a decoded track. A channel kept as a single key holds at that key, and a channel
     * with no keys at all falls back to whatever it is being read against. */
    private static FTransform ReadKey(DecodedTrack track, int frame, FTransform fallback)
    {
        var translation = fallback.Translation;
        var rotation = fallback.Rotation;
        var scale = fallback.Scale3D;

        var positionKeys = track.Positions.Length / 3;
        var rotationKeys = track.Rotations.Length / 4;
        var scaleKeys = track.Scales.Length / 3;

        if (positionKeys > 0)
        {
            var key = Math.Clamp(frame, 0, positionKeys - 1);

            translation = new FVector(track.Positions[key * 3], track.Positions[key * 3 + 1], track.Positions[key * 3 + 2]);
        }

        if (rotationKeys > 0)
        {
            var key = Math.Clamp(frame, 0, rotationKeys - 1);

            rotation = new FQuat(track.Rotations[key * 4], track.Rotations[key * 4 + 1], track.Rotations[key * 4 + 2], track.Rotations[key * 4 + 3]);
        }

        if (scaleKeys > 0)
        {
            var key = Math.Clamp(frame, 0, scaleKeys - 1);

            scale = new FVector(track.Scales[key * 3], track.Scales[key * 3 + 1], track.Scales[key * 3 + 2]);
        }

        return new FTransform(rotation, translation, scale);
    }

    [DllImport(ACLNative.LIB_NAME)]
    private static extern unsafe void nReadACLData(IntPtr compressedTracks, FTransform* inRefPoses, FTrackToSkeletonMap* inTrackToSkeletonMap, FTransform* outAtom);
}
