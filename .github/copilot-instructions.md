# Files - Windows File Manager

Files is a modern Windows UWP/WinUI file manager built with .NET 9.0, C#, and XAML. It features robust multitasking, file tags, deep Windows integrations, and Git repository support.

**CRITICAL**: Always reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.

## Platform Requirements

**WINDOWS ONLY**: This application can ONLY be built and run on Windows. Do not attempt to build on Linux or macOS.

- **Operating System**: Windows 10 version 19041.0 (build 19041) or later
- **Target Windows Version**: 10.0.26100.0  
- **Framework**: .NET 9.0.200 (required version from global.json)
- **Build Tools**: Visual Studio 2022 with Windows SDK 10.0.26100.67-preview
- **MSBuild**: Required for building (part of Visual Studio or Build Tools)

## Working Effectively

### Initial Setup
1. **Install .NET 9.0 SDK** (REQUIRED): Download .NET 9.0.200 exactly from https://dotnet.microsoft.com/download/dotnet/9.0
2. **Install Visual Studio 2022** with:
   - .NET desktop development workload
   - Universal Windows Platform development workload  
   - Windows SDK 10.0.26100.67-preview
3. **Install Windows Application Driver** for UI tests: Download from https://github.com/microsoft/WinAppDriver/releases

### Build Process
- **NEVER CANCEL BUILDS**: Build process takes 15-45 minutes depending on configuration. Set timeouts to 60+ minutes minimum.
- **Restore packages**: `msbuild Files.slnx -t:Restore -p:Platform=x64 -p:Configuration=Release -v:quiet` -- takes 2-5 minutes
- **Build solution**: `msbuild "src/Files.App (Package)/Files.Package.wapproj" -t:Build -p:Configuration=Release -p:Platform=x64 -p:AppxBundle=Never -v:quiet` -- takes 15-30 minutes. NEVER CANCEL.
- **Package for testing**: Add `-t:_GenerateAppxPackage` for full packaging -- takes 30-45 minutes. NEVER CANCEL.

### Testing
- **Unit/Integration Tests**: `dotnet test` on Files.InteractionTests.dll -- takes 10-15 minutes. NEVER CANCEL.
- **UI Tests require**: WinAppDriver running and application installed on local machine
- **Test timeout**: Set 30+ minute timeouts for test execution

## Key Projects Structure

### Core Projects
- **Files.App**: Main application (WinUI 3, C#)
- **Files.App (Package)**: UWP packaging project (Files.Package.wapproj)
- **Files.Core.Storage**: Storage abstractions and implementations
- **Files.App.Storage**: File system operations
- **Files.Shared**: Shared utilities and models

### Platform Integration
- **Files.App.Launcher**: Native Win32 launcher (C++)
- **Files.App.OpenDialog**: File open dialog integration (C++)
- **Files.App.SaveDialog**: File save dialog integration (C++)
- **Files.App.Server**: Background service for file operations

### Supporting Libraries
- **Files.App.Controls**: Custom WinUI controls
- **Files.App.BackgroundTasks**: Background processing
- **Files.Core.SourceGenerator**: Code generation for commands

### Test Projects
- **Files.InteractionTests**: UI automation tests (WinAppDriver + MSTest)
- **Files.App.UITests**: Additional UI test scenarios

## Validation Requirements

### Before Committing Changes
1. **XAML Formatting**: Run `dotnet tool install --global XamlStyler.Console` then:
   - `xstyler -l None -r -d src/Files.App`
   - `xstyler -l None -r -d src/Files.App.Controls` 
   - `xstyler -l None -r -d tests/Files.App.UITests`
2. **Build Validation**: Ensure clean build with your changes
3. **Manual Testing**: Test actual file manager functionality - open files, navigate folders, use context menus

### Manual Testing Scenarios
After making changes, ALWAYS test these core scenarios:
1. **File Navigation**: Open Files app, navigate to different folders (Documents, Desktop, etc.)
2. **File Operations**: Create/rename/delete files and folders
3. **Context Menus**: Right-click on files/folders, verify menus work
4. **Tab Management**: Open multiple tabs, close tabs, navigate between tabs
5. **Git Integration** (if applicable): Navigate to git repository, verify git status displays
6. **Search**: Use search functionality to find files

## Common Issues and Solutions

### Build Issues
- **"Windows targeting not supported"**: You're not on Windows - this project requires Windows
- **Missing Windows SDK**: Install Windows SDK 10.0.26100.67-preview through Visual Studio Installer
- **NuGet restore failures**: Check network connectivity and NuGet package sources in nuget.config
- **C++ project build failures**: Ensure Visual Studio C++ tools are installed

### Test Issues  
- **WinAppDriver not found**: Install from https://github.com/microsoft/WinAppDriver/releases
- **UI tests fail to start app**: Ensure Files.Package is built and can be installed locally
- **Test timeouts**: UI tests can take 15+ minutes, set appropriate timeouts

## Timing Expectations

**CRITICAL - NEVER CANCEL THESE OPERATIONS**:
- **NuGet Restore**: 2-5 minutes
- **Clean Build**: 15-30 minutes  
- **Package Build**: 30-45 minutes
- **Full CI Build**: 45-60 minutes
- **UI Test Suite**: 10-15 minutes
- **Manual App Launch**: 30-60 seconds first time, 10-15 seconds subsequent

## File Locations and Navigation

### Key Configuration Files
- `Files.slnx`: Solution file (MSBuild format)
- `global.json`: .NET SDK version requirement  
- `Directory.Build.props`: Common build properties
- `nuget.config`: Package source configuration

### Important Directories
- `src/Files.App/`: Main application source
- `src/Files.App (Package)/`: UWP packaging
- `src/Files.App/Actions/`: Command implementations  
- `src/Files.App/Utils/Git/`: Git integration (GitHelpers.cs)
- `src/Files.App/Views/`: UI views and pages
- `tests/Files.InteractionTests/`: UI automation tests
- `.github/workflows/`: CI/CD pipeline definitions

### XAML Files
- Located primarily in `src/Files.App/Views/` and `src/Files.App.Controls/`
- Always format with XamlStyler before committing
- Follow existing XAML code style and patterns

## Git Integration Notes

Files includes extensive Git repository integration:
- **GitHelpers.cs**: Core Git operations (`src/Files.App/Utils/Git/GitHelpers.cs`)
- **Git Actions**: Repository initialization, branch management (`src/Files.App/Actions/Git/`)
- **Status Display**: Shows Git status in file manager UI
- Uses LibGit2Sharp library for Git operations

When modifying Git features, always test with actual Git repositories to ensure functionality works correctly.

## CI Pipeline (.github/workflows/ci.yml)

The CI runs on Windows and includes:
1. **XAML Format Check**: Validates XAML formatting
2. **Multi-Platform Build**: Builds for x64, arm64 platforms  
3. **Package Generation**: Creates installable packages
4. **UI Test Execution**: Runs automated UI tests with WinAppDriver

**Key CI Commands** (reference only - these run on Windows CI):
```bash
# Restore
msbuild Files.slnx -t:Restore -p:Platform=x64 -p:Configuration=Release

# Build  
msbuild "src/Files.App (Package)/Files.Package.wapproj" -t:Build -p:Configuration=Release -p:Platform=x64

# Test
dotnet test artifacts/TestsAssembly/**/Files.InteractionTests.dll --logger "trx"
```

## Development Notes

- **Source Generation**: Project uses Roslyn source generators for command management
- **Dependency Injection**: Uses Microsoft.Extensions.Hosting with IServiceCollection
- **MVVM Pattern**: ViewModels are located alongside Views
- **Async/Await**: Extensive use of async operations for file I/O
- **Resource Management**: Uses Windows.ApplicationModel.Resources for localization

When adding new features, follow existing patterns for dependency injection, async operations, and MVVM architecture.