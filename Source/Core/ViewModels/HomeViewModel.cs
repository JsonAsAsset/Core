using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;

using Core.Framework.Models;

namespace Core.ViewModels;

public class HomeViewModel : ViewModelBase
{
    private readonly Random _random = new();

    public readonly string[] TagLines =
    [
        "buy us some time (and coffee ☕)",
        "developed by a dedicated community of contributors",
        "bringing clarity to the chaos",
        "accidentally pushed into prod",
        "developed with a lot of coffee ☕",
        "powered by frustration and breakthroughs",
        "we speak fluent .uasset",
        "we run on hope and async",
        "open-source, open-minds",
        "todo: fix this - 1993",
        "we outlived the docs",
        "built on caffeine and curiosity",
        "WAKE UP, WAKE UP, ITS THE FIRST OF THE MONTH",
        "◡‿◡",
        "ᓚ₍ ^. .^₎",
        "half code, half ritual",
        ":DDDDDDDDDD",
        "ᓚᘏᗢ",
        "▲ for your pardon",
        "ㅇㅅㅇ",
        "▲",
        "thank you!",
        "we appreciate you!"
    ];
    
    public readonly string[] Tips =
    [
        "Hover over buttons to see if they have keybinds",
        "Shortcuts make everything faster. Use them",
        "Keyboard shortcuts are your best friend",
        "This tool is community-driven — feedback matters!",
        "Check the logs — they're full of secrets"
    ];
    
    public async void StartRotation(string[] phrases, int rotateTime, TextBlock textBlock, Control? fadingControl = null!, bool useRandom = false)
    {
        if (phrases.Length == 0) return;

        var index = 0;
        var lastIndex = -1;
        fadingControl ??= textBlock;

        var initialPhrase = useRandom ? GetRandomPhrase(phrases, lastIndex, out lastIndex) : phrases[index];
        
        fadingControl.Opacity = 0;
        textBlock.Text = initialPhrase;
        await FadeIn(fadingControl);
        
        if (!useRandom) index++;

        while (true)
        {
            await Task.Delay(rotateTime);

            string nextPhrase;
            if (useRandom)
            {
                nextPhrase = GetRandomPhrase(phrases, lastIndex, out lastIndex);
            }
            else
            {
                nextPhrase = phrases[index % phrases.Length];
                index++;
            }

            await FadeOut(fadingControl);
            textBlock.Text = nextPhrase;
            await FadeIn(fadingControl);
        }
    }
    
    private string GetRandomPhrase(string[] phrases, int lastIndex, out int newIndex)
    {
        if (phrases.Length == 1)
        {
            newIndex = 0;
            return phrases[0];
        }

        var availableIndices = Enumerable.Range(0, phrases.Length)
            .Where(i => i != lastIndex)
            .ToArray();

        newIndex = availableIndices[_random.Next(availableIndices.Length)];
        return phrases[newIndex];
    }
    
    private static async Task FadeOut(Control control)
    {
        const int steps = 10;
        const int delay = 25;

        for (var i = 0; i < steps; i++)
        {
            control.Opacity = 1 - (i / (float)steps);
            await Task.Delay(delay);
        }

        control.Opacity = 0;
    }

    private static async Task FadeIn(Control control)
    {
        const int steps = 10;
        const int delay = 25;

        for (var i = 0; i < steps; i++)
        {
            control.Opacity = i / (float)steps;
            await Task.Delay(delay);
        }

        control.Opacity = 1;
    }
}
