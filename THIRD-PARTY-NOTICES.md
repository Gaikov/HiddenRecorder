# Third-party components

The portable package contains unmodified, dynamically linked third-party components.

- .NET 10 — Microsoft / .NET Foundation, MIT and component-specific notices in the published runtime.
- Windows App SDK / WinUI 3 — Microsoft, Microsoft distribution terms and bundled component licenses. NuGet packages retain their LICENSE/NOTICE files.
- CommunityToolkit.Mvvm — .NET Foundation, MIT.
- GStreamer 1.26.11 (MSVC x64) — GStreamer contributors, LGPL-2.1-or-later for core and the selected GStreamer plugins. Upstream distribution: https://gstreamer.freedesktop.org/data/pkg/windows/1.26.11/msvc/ . Corresponding source releases: https://gstreamer.freedesktop.org/src/ . Dependency source/build recipes: https://gitlab.freedesktop.org/gstreamer/cerbero/-/tree/1.26.11 . Runtime dependency license texts are included under `gstreamer/share/licenses`.
- OpenH264 — Cisco and contributors, BSD-2-Clause; see the bundled OpenH264 license and upstream https://github.com/cisco/openh264 . Patent licensing considerations are separate from copyright licenses.
- Visual C++ runtime — Microsoft, redistributed under the Visual Studio REDIST terms.

Libraries remain separate DLLs and may be replaced by ABI-compatible builds. The application does not statically link GStreamer. The build scripts and source show the versions, linking and package layout. Before redistributing commercially, review the included third-party terms for your distribution and jurisdiction.

Build/test-only dependencies (not shipped as application features): CMake, Python, xUnit, Microsoft.NET.Test.Sdk, Microsoft.Extensions.TimeProvider.Testing, PyAV (optional media validation).
