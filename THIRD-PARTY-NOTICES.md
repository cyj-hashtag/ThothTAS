# Third-party components

The release bundles the .NET 10 runtime, including Windows Forms. Its license is reproduced in [licenses/dotnet-MIT.txt](licenses/dotnet-MIT.txt); additional notices are reproduced in [licenses/dotnet-THIRD-PARTY-NOTICES.txt](licenses/dotnet-THIRD-PARTY-NOTICES.txt).

Build dependencies include [Mono.Cecil](https://github.com/jbevain/cecil) and Microsoft .NET Framework 3.5 reference assemblies obtained through NuGet. Their source and licenses remain with their respective projects.

[BepInEx 5.4.23.5 x86](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) is downloaded separately by the copy-creation script, checked against a pinned SHA256, and installed only in the local TAS game copy.

THOTH, Unity game assemblies and game assets are supplied by the user's local game installation. They are not included in the public repository or the public tool release.
