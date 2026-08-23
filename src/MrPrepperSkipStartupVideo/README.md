# Mr. Prepper Skip Startup Video

Small BepInEx 5 plugin for Mr. Prepper that fast-forwards only the startup `RGintro` video in the `LoadingScreen` scene.

It deliberately targets all three identifiers together:

- scene: `LoadingScreen`
- GameObject: `RGintro`
- VideoClip: `RGintro`

It does not touch other VideoPlayers, including the separate `Main16/UI/Canvas/Intro` video.

## Build

```powershell
dotnet build .\src\MrPrepperSkipStartupVideo\MrPrepperSkipStartupVideo.csproj -c Release
```

The build stages the DLL to `dist/MrPrepperSkipStartupVideo` and to the game's BepInEx plugins directory.
