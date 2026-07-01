using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using EpicManifestParser.UE;

namespace Core.Resources.Framework.Base;

public class BaseOnDemandProvider : BaseProvider
{
    public FBuildPatchAppManifest? LiveManifest;

    private readonly IEnumerable<DirectoryInfo> OnDemandFolders = [];
    public bool LoadOnDemandFolders = true;
    
    public BaseOnDemandProvider(
        string directory,
        VersionContainer? version
    ) : base(directory, version)
    {
    }
    
    public BaseOnDemandProvider(string directory, List<DirectoryInfo>? extraDirectories = null, VersionContainer? version = null) : base(directory, version)
    {
        OnDemandFolders = extraDirectories?.Where(dir => dir.Exists) ?? [];
    }
    
    public override void Initialize()
    {
        base.Initialize();

        if (!LoadOnDemandFolders)
        {
            return;
        }

        foreach (var folder in OnDemandFolders)
        {
            RegisterFiles(folder);
        }
    }

    public void RegisterFiles(DirectoryInfo directory)
    {
        if (OnDemandOptions is null) return;

        foreach (var file in directory.EnumerateFiles("*.*", EnumerationOptions))
        {
            var extension = file.Extension.SubstringAfter('.').ToLower();

            switch (extension)
            {
                case "pak":
                case "utoc":
                {
                    RegisterVfs(
                        file.FullName,
                        [file.OpenRead()],
                        path => new FStreamArchive(path, File.OpenRead(path), Versions)
                    );
                    break;
                }

                case "uondemandtoc":
                {
                    var toc = new IoChunkToc(file.FullName, Versions);
                    break;
                }
            }
        }
    }

    public void RegisterFiles(FBuildPatchAppManifest manifest)
    {
        if (OnDemandOptions is null) return;
        
        var cacheDirectory = Path.Combine(
            Globals.OnDemandFolder.FullName,
            "uondemandtoc",
            manifest.Meta.BuildVersion
        );

        Directory.CreateDirectory(cacheDirectory);

        foreach (var file in manifest.Files)
        {
            if (!file.FileName.Contains("FortniteGame/Content/Paks"))
            {
                continue;
            }

            var extension = file.FileName.SubstringAfter('.').ToLower();

            switch (extension)
            {
                case "pak":
                case "utoc":
                {
                    RegisterVfs(
                        file.FileName,
                        new Stream[] { file.GetStream() },
                        name => new FStreamArchive(
                            name,
                            manifest.Files.First(x => x.FileName.Equals(name)).GetStream()
                        )
                    );
                    break;
                }

                case "uondemandtoc":
                {
                    var targetPath = Path.Combine(
                        cacheDirectory,
                        file.FileName.SubstringAfterLast("/")
                    );

                    if (!File.Exists(targetPath))
                    {
                        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
                        file.GetStream().CopyTo(fs);
                    }

                    var toc = new IoChunkToc(targetPath, Versions);
                    break;
                }
            }
        }
    }
}