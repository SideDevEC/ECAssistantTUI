# VS 2022 Setup Guide — ECAssistant

## First Time Setup

1. Clone ECAssistant repo
2. Build ECAssistantCore on your machine (`dotnet build -c Release`)
3. Copy `ECAssistantCore/bin/Release/net8.0/ECAssistant.Core.dll` to `ECAssistant/lib/`
4. Open `ECAssistant.sln` (or `.slnx`) in Visual Studio 2022
5. Ensure .NET SDK 8.0.x is installed
6. Right-click `ECAssistant` project → **Set as Startup Project**
7. `dotnet restore` then F5

## Common Issues

### NETSDK1141: Unable to resolve .NET SDK version
- `global.json` pins to SDK 8.0.x with `rollForward: latestFeature`
- Install any .NET 8.0.x SDK (e.g., 8.0.100, 8.0.424)

### Unable to start debugging / startup project could not be launched
- Delete the `.vs/` folder in the solution root
- Close VS → delete `.vs/` → reopen
- VS caches debug state and it gets stale after structural changes

### System.Text.Json could not load file or assembly
- LLamaSharp 0.27.0 transitively requires System.Text.Json 10.0.4
- net8.0 ships with 8.x — explicit package reference in csproj fixes it
- Already in csproj: `<PackageReference Include="System.Text.Json" Version="10.0.4" />`

### LLamaSharp could not load file or assembly
- App needs the `LLamaSharp` managed package (not just backend packages)
- Backend packages only provide native binaries (llama.dll etc)
- The managed LLamaSharp.dll is needed because Core.dll references it at runtime
- Already in csproj: `<PackageReference Include="LLamaSharp" Version="0.27.0" />`

### Green play button doesn't work but F5 does
- Delete `.vs/` folder, reopen solution
- Ensure `e-assistant` is selected in the startup dropdown

## Dependencies in App

- `lib/ECAssistant.Core.dll` — gitignored, build locally from ECAssistantCore
- `LLamaSharp` 0.27.0 — managed DLL (runtime dependency of Core)
- `LLamaSharp.Backend.Cpu/Vulkan/Cuda12` — native llama binaries (auto-copied to output)
- `System.Text.Json` 10.0.4 — transitive dependency from LLamaSharp

App code never touches LLamaSharp types. These are runtime dependencies only.

## Build Order

1. Build ECAssistantCore (`dotnet build -c Release`)
2. Copy DLL to `lib/`
3. Build ECAssistant (F5 or `dotnet build`)

**Added:** 2026-08-16