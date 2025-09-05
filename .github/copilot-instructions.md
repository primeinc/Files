# Files - Windows File Manager

Files is a modern UWP/WinUI Windows file manager application built with .NET 9, C#, and C++. It provides a rich, modern file management experience with Git integration, tags, and extensive customization options.

Always reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.

## Working Effectively

### Prerequisites (CRITICAL - Windows Only)
Files is a **Windows-only application** that requires:
- **Windows 10 version 19041.0 (May 2020 Update) or later**
- **Visual Studio 2022 version 17.0 or later** with:
  - .NET Desktop Development workload
  - Universal Windows Platform development workload  
  - Desktop development with C++ workload
- **.NET 9.0.200 SDK** (as specified in global.json)
- **Windows SDK 10.0.26100.0** (minimum 10.0.19041.0)
- **MSBuild** (included with Visual Studio)
- **NuGet** (included with Visual Studio)

### Build and Restore Commands
Run these commands in the repository root. **NEVER CANCEL these commands** - they take significant time:

```bash
# Restore packages - takes 2-5 minutes, set timeout to 10+ minutes
msbuild Files.slnx -t:Restore -p:Platform=x64 -p:Configuration=Release -p:PublishReadyToRun=true -v:quiet

# Build the application - takes 15-45 minutes, NEVER CANCEL, set timeout to 60+ minutes  
msbuild "src/Files.App (Package)/Files.Package.wapproj" -t:Build -p:Configuration=Release -p:Platform=x64 -p:AppxBundle=Never -v:quiet
```

Alternative using dotnet CLI (if MSBuild not available):
```bash
# Note: This may not work due to Windows-specific components
dotnet restore -p:EnableWindowsTargeting=true
dotnet build -p:EnableWindowsTargeting=true
```

### Testing
- **Unit Tests**: Run from Visual Studio Test Explorer or:
  ```bash
  # Build and run interaction tests - takes 10-15 minutes, NEVER CANCEL, set timeout to 30+ minutes
  dotnet test tests/Files.InteractionTests/Files.InteractionTests.csproj --configuration Release
  ```

- **UI Tests**: Require WinAppDriver installation:
  ```bash
  # Download and install WinAppDriver from:
  # https://github.com/microsoft/WinAppDriver/releases
  ```

### Package and Deploy
```bash
# Create APPX package for sideloading - takes 20-30 minutes, NEVER CANCEL, set timeout to 45+ minutes
msbuild "src/Files.App (Package)/Files.Package.wapproj" -t:Build -t:_GenerateAppxPackage -p:Configuration=Release -p:Platform=x64 -p:AppxBundlePlatforms=x64 -p:AppxBundle=Always -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageDir=artifacts/AppxPackages -v:quiet
```

### Code Quality
Always run these before committing:
```bash
# Install XAML formatting tool (one-time setup)
dotnet tool install --global XamlStyler.Console

# XAML formatting check - takes 1-2 minutes, set timeout to 5+ minutes
# Settings are defined in Settings.XamlStyler (uses tabs, specific markup extensions)
xstyler -p -l None -f [xaml-file-path]

# Check changed XAML files in Git workflow:
git diff --diff-filter=d --name-only HEAD~1 | grep "\.xaml$" | xargs -I {} xstyler -p -l None -f {}
```

### Code Style Standards
- **Indentation**: Use **tabs** (not spaces) for C# and XAML files
- **Tab width**: 4 characters (configured in .editorconfig)
- **XAML formatting**: Enforced by XamlStyler with custom settings in Settings.XamlStyler
- **C# formatting**: Follow standard .NET conventions

## Validation Scenarios

### CRITICAL Build Time Expectations
- **NEVER CANCEL builds or long-running commands**
- **Package restore**: 2-5 minutes (set timeout to 10+ minutes)
- **Full build**: 15-45 minutes (set timeout to 60+ minutes)  
- **Package creation**: 20-30 minutes (set timeout to 45+ minutes)
- **Test execution**: 10-15 minutes (set timeout to 30+ minutes)

### Manual Validation Requirements
After making changes, ALWAYS validate by:
1. **Build the solution completely** - do not skip this step
2. **Run the packaged application** to verify it starts correctly
3. **Test basic file operations**: navigation, copy, paste, delete
4. **Test Git integration** if changes affect Git functionality  
5. **Verify no accessibility regressions** using built-in accessibility tools

### Functional Testing Scenarios
When testing the application:
- **Open Files and navigate** to different folders
- **Test context menus** by right-clicking files/folders
- **Verify tab functionality** by opening multiple tabs
- **Test file operations**: copy, move, delete, rename
- **Check Git integration** if in a Git repository
- **Verify settings and preferences** are accessible and functional

## Project Structure

### Key Components
- **`src/Files.App/`** - Main WinUI application (C#)
- **`src/Files.App (Package)/`** - Windows Application Packaging project (.wapproj)
- **`src/Files.App.Server/`** - Background server component
- **`src/Files.Core.Storage/`** - Core storage abstractions
- **`src/Files.Shared/`** - Shared utilities and extensions
- **`src/Files.App.Launcher/`** - Native launcher (C++)
- **`src/Files.App.OpenDialog/`** - File picker integration (C++)
- **`src/Files.App.SaveDialog/`** - Save dialog integration (C++)

### Important Files to Monitor
- **`Files.slnx`** - Modern solution file format
- **`Directory.Build.props`** - Global build properties (.NET 9, Windows SDK versions)
- **`Directory.Packages.props`** - Centralized package management
- **`global.json`** - .NET SDK version specification (9.0.200)
- **`nuget.config`** - Package sources including CommunityToolkit Labs feed
- **`.editorconfig`** - Code formatting rules (tabs, indent size)
- **`Settings.XamlStyler`** - XAML formatting configuration
- **`src/Files.App/app.manifest`** - Windows app manifest (DPI awareness, long path support)

### Git Integration Code
The application has extensive Git functionality:
- **`src/Files.App/Utils/Git/GitHelpers.cs`** - Core Git operations
- **`src/Files.App/Actions/Git/`** - Git-related actions (clone, pull, push, init)

## Common Development Tasks

### Adding New Features
1. **Always build and test first** to establish baseline
2. **Follow the existing project structure** and naming conventions
3. **Use dependency injection** (Ioc.Default.GetRequiredService<>())
4. **Implement IAction interface** for new commands/actions
5. **Add proper localization** using Strings.*.GetLocalizedResource()

### Debugging Git Features
- **Git operations** are centralized in GitHelpers.cs
- **Authentication** uses Windows Credential Manager
- **Repository detection** follows parent directory traversal pattern
- **Always test with actual Git repositories** for realistic scenarios

### Working with UI Components
- **Uses WinUI 3** with modern XAML controls
- **CommunityToolkit components** for enhanced functionality
- **XAML formatting** is automatically enforced via CI
- **Accessibility** is critical - test with screen readers

## Build Troubleshooting

### Common Issues
- **"NETSDK1100" error**: Only build on Windows - Linux/macOS not supported
- **NuGet restore failures**: Check internet connection and package feeds
- **C++ build failures**: Ensure Visual Studio C++ workload is installed
- **XAML compilation errors**: Run XamlStyler for formatting fixes
- **Package signing errors**: Use development certificates for local builds

### Package Sources
The project uses these NuGet feeds (configured in nuget.config):
- **NuGet Gallery**: https://api.nuget.org/v3/index.json
- **Toolkit Labs**: https://pkgs.dev.azure.com/dotnet/CommunityToolkit/_packaging/CommunityToolkit-Labs/nuget/v3/index.json
- **Project Packages**: src/Files.App/Assets/Libraries/ (for SevenZipSharp)

## Limitations and Constraints

### Platform Restrictions
- **Windows-only development** - cannot build on Linux/macOS
- **Requires Visual Studio** - VS Code insufficient for full development
- **UWP/WinUI dependencies** - needs Windows 10+ runtime
- **C++ components** - require MSVC compiler toolchain

### CI/CD Integration
The GitHub Actions workflow (`.github/workflows/ci.yml`) provides the authoritative build process:
- **Multi-platform builds**: x64, x86, arm64
- **Automated testing** with WinAppDriver
- **XAML formatting validation**
- **Package generation** for sideloading

Always align local development practices with the CI pipeline for consistency.