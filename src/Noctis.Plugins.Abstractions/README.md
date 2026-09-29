# Noctis.Plugins.Abstractions

The plugin kit for [Noctis](https://github.com/heartached/Noctis), a desktop music player.
Reference it with `ExcludeAssets="runtime"`: the app supplies this assembly (and Avalonia) at run time.

```xml
<PackageReference Include="Noctis.Plugins.Abstractions" Version="1.1.*" ExcludeAssets="runtime" PrivateAssets="all" />
```

Implement `INoctisPlugin`, add a `plugin.json` manifest next to your DLL, zip the folder and
install it from Settings → Plugins → Install from file…

Full guide (manifest reference, API, permissions, safety model, packaging):
https://github.com/heartached/Noctis/blob/main/docs/PLUGINS.md
