# GitHub Workflows for Fork

## Available Workflows

### 1. `ci-fork.yml` (Simplified CI)
- **Purpose**: Basic build validation without requiring secrets
- **Triggers**: Push to main/feature/fix branches, Pull Requests
- **What it does**:
  - Builds the solution in Debug and Release modes
  - Runs tests (continues on error)
  - Uploads build artifacts
  - Checks code formatting
  - Runs code analysis

### 2. `ci.yml` (Original CI)
- **Purpose**: Full CI with all checks
- **Note**: Modified to work with `primeinc` fork
- **Requirements**: Some jobs may need secrets configured

## Setting Up Secrets (Optional)

If you want to run the full CI/CD workflows, you'll need to configure these secrets in your repository settings:

### For Sideload builds:
- `SIDELOAD_PUBLISHER_SECRET`
- `BING_MAPS_SECRET`
- `SENTRY_SECRET`
- `GH_OAUTH_CLIENT_ID`

### For Store builds:
- `STORE_PUBLISHER_SECRET`
- `AZURE_TENANT_ID`
- `AZURE_CLIENT_ID`
- `AZURE_CLIENT_SECRET`

### For Code Signing:
- `SIGNING_ACCOUNT_NAME`
- `SIGNING_PROFILE_NAME`

## Disabling Workflows

If you don't need certain workflows, you can disable them:

1. Go to Actions tab in your repository
2. Click on the workflow you want to disable
3. Click "..." menu → "Disable workflow"

## Recommended Setup for Fork

1. Use `ci-fork.yml` for basic build validation
2. Disable CD workflows unless you plan to deploy
3. Disable `format-xaml.yml` unless you have the bot configured

## Troubleshooting

### Build Fails with Missing Secrets
- Use `ci-fork.yml` instead which doesn't require secrets
- Or add placeholder values for the required secrets

### Tests Fail
- Tests are set to `continue-on-error: true` in fork CI
- Check test output for actual issues

### Workflow Not Running
- Check if the branch/path triggers match your changes
- Ensure workflows are enabled in your fork's Actions settings