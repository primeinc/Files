# GitHub Copilot Instructions for Files App

## Project Overview
Files is a Windows file manager application built with:
- **.NET 9**: Core framework
- **WinUI 3 / UWP**: User interface framework
- **XAML**: UI markup language
- **C#**: Primary programming language

## Important Notes for Development

### Platform Limitations
- This is a **Windows-specific application** that requires Windows and Visual Studio/MSBuild for full builds
- GitHub Copilot coding agent runs on **Ubuntu Linux**, which limits what can be built and tested
- Focus on cross-platform compatible code changes when possible

### Development Environment
When working in the Copilot environment:
- **.NET 9 SDK** is available for code analysis and basic operations
- **NuGet package restore** may partially fail for Windows-specific packages (this is expected)
- **XAML formatting** can be checked using XamlStyler
- **Code analysis** tools like dotnet-format are available

### Code Style Guidelines

#### C# Code
- Follow existing C# coding conventions in the codebase
- Use file-scoped namespaces where appropriate
- Prefer modern C# features (pattern matching, null-conditional operators, etc.)
- Use async/await for asynchronous operations

#### XAML Code
- Follow the XamlStyler configuration in `Settings.XamlStyler`
- Maintain consistent indentation and formatting
- Use resource dictionaries for reusable styles and templates

#### General
- **DO NOT** add comments unless specifically requested
- Follow existing patterns and conventions in neighboring files
- Check imports and dependencies before adding new libraries

### Building and Testing

Since full builds aren't possible on Linux, focus on:
1. **Code correctness**: Ensure syntax and logic are correct
2. **Style compliance**: Use dotnet-format and XamlStyler for formatting
3. **Pattern consistency**: Match existing code patterns in the repository

### Common Tasks

#### When modifying C# files:
```bash
# Format C# code
dotnet format --include <file-path>

# Check for basic compilation errors (may not work for all files)
dotnet build <project>.csproj || true
```

#### When modifying XAML files:
```bash
# Format XAML
xstyler -p -l None -f <file-path>
```

### Working with Dependencies
- Check `Directory.Packages.props` for centralized package versions
- Respect the package version management strategy
- Don't add new packages without checking if similar functionality already exists

### Security Considerations
- Never hardcode sensitive information
- Don't expose API keys or credentials
- Follow secure coding practices for file operations

## Project Structure

```
Files/
├── src/
│   ├── Files.App/           # Main application
│   ├── Files.App (Package)/ # Package project
│   ├── Files.App.CsWin32/   # Win32 interop
│   ├── Files.App.Storage/   # Storage abstraction
│   └── Files.Core/          # Core functionality
├── tests/
│   └── Files.InteractionTests/ # UI interaction tests
└── .github/
    └── workflows/            # CI/CD pipelines
```

## Helpful Commands

```bash
# View project structure
tree -d -L 2 src/

# Find specific file types
find . -name "*.cs" | grep -i <search-term>

# Search code content
rg "pattern" --type cs

# List all project files
find . -name "*.csproj"
```

## Common Issues and Solutions

1. **Package restore failures**: Expected on Linux for Windows-specific packages
2. **Build errors**: Focus on code correctness rather than successful compilation
3. **XAML IntelliSense**: Not available on Linux, verify XAML syntax manually

## Testing Approach

While full testing isn't possible on Linux:
1. Validate code syntax and logic
2. Ensure consistent code style
3. Check for obvious errors or anti-patterns
4. Verify imports and namespaces are correct

## Additional Resources

- Main branch for PRs: `main`
- Solution file: `Files.slnx`
- Global .NET configuration: `global.json`
- Package versions: `Directory.Packages.props`