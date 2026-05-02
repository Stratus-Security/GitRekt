# GitRekt

GitRekt is a GitHub code-search triage tool for finding possible exposed secrets, credentials, and sensitive configuration hints across public or authorized repositories.

It wraps GitHub code search with practical output, line links, rate-limit handling, optional GitHub App authentication, and optional AI validation to reduce false positives.

> GitRekt is a triage tool. Treat findings as leads to review, not proof that a secret is valid or exploitable.

## Features

- Search GitHub code with one or more queries.
- Use simple search strings or pass advanced GitHub search syntax unchanged.
- Print highlighted snippets and direct GitHub file links.
- Resolve line numbers when possible.
- Authenticate with a GitHub token or GitHub App installation token.
- Refresh GitHub App installation tokens during long scans.
- Handle GitHub primary and secondary rate limits with clearer wait/error messages.
- Optionally validate results with AI:
  - Ollama
  - Ollama agent mode with same-repository evidence gathering
  - Google Gemini
  - OpenAI
- Cache file content, repository trees, duplicate snippets, and AI validation work within a run.
- Publish as NativeAOT single-file executables for Windows, Linux, and macOS.

## Download

Download prebuilt binaries from the [GitHub Releases page](https://github.com/Stratus-Security/GitRekt/releases).

Release assets are built as NativeAOT single executables:

- `gitrekt-win-x64.zip`
- `gitrekt-linux-x64.tar.gz`
- `gitrekt-osx-x64.tar.gz`
- `gitrekt-osx-arm64.tar.gz`

After extracting on Linux or macOS, make the binary executable if needed:

```bash
chmod +x GitRekt
```

## Build From Source

Requirements:

- .NET 10 SDK
- NativeAOT prerequisites for your OS

Build:

```bash
dotnet build GitRekt.slnx -c Release
```

Publish a local NativeAOT executable:

```bash
dotnet publish GitRekt/GitRekt.csproj -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:PublishSingleFile=true
```

Use the runtime identifier that matches your platform, for example `win-x64`, `linux-x64`, `osx-x64`, or `osx-arm64`.

## Basic Usage

Search for a simple string:

```bash
GitRekt --query "Password1"
```

Search for multiple terms in one run:

```bash
GitRekt --query "Password1" --query "Password2" --query "@example.com"
```

Write output to a file:

```bash
GitRekt --query "Password1" --output results.txt
```

Use advanced GitHub code-search syntax exactly as written:

```bash
GitRekt --advanced --query "\"Password1\" language:C# path:/src/"
```

## GitHub Authentication

Unauthenticated GitHub searches are very limited. For realistic use, authenticate.

### Personal Access Token

Set an environment variable:

```bash
export GITHUB_ACCESS_TOKEN="ghp_..."
GitRekt --query "Password1"
```

Or pass it directly:

```bash
GitRekt --token "ghp_..." --query "Password1"
```

### GitHub App

GitHub App auth is a better fit for longer scans because installation tokens can be refreshed and scoped to the installed account.

Set:

```bash
export GITHUB_APP_ID="12345"
export GITHUB_APP_INSTALLATION_ID="67890"
export GITHUB_APP_PRIVATE_KEY_PATH="/path/to/app.private-key.pem"

GitRekt --query "Password1"
```

If the app has exactly one installation, `GITHUB_APP_INSTALLATION_ID` is optional. GitRekt can also pick up exactly one `*.private-key.pem` file from the current directory when `GITHUB_APP_ID` is set.

Equivalent CLI flags:

```bash
GitRekt \
  --github-app-id 12345 \
  --github-app-installation-id 67890 \
  --github-app-private-key-path app.private-key.pem \
  --query "Password1"
```

## AI Validation

AI validation can classify each displayed result as:

- `likely_sensitive`
- `possible_sensitive_lead`
- `no_sensitive_evidence`

You can filter output by verdict:

```bash
GitRekt --query "Password1" --ai --ai-model llama3.2 --ai-verdict yellow
```

Verdict aliases:

- `likely`, `red`
- `possible`, `yellow`
- `none`, `green`

### Ollama

Ollama is the default AI provider.

```bash
GitRekt --query "Password1" --ai --ai-model llama3.2
```

Agent mode lets the model inspect same-repository context using bounded read-only tools:

```bash
GitRekt --query "Password1" --ai-agent --ai-model llama3.2 --ai-verdict yellow
```

Agent mode is currently Ollama-only.

### Gemini

```bash
export GEMINI_API_KEY="..."

GitRekt --query "Password1" --ai-provider gemini --ai-model gemini-2.5-flash
```

You can also use `GOOGLE_API_KEY`, `--ai-api-key`, or `--gemini-api-key`.

### OpenAI

```bash
export OPENAI_API_KEY="..."

GitRekt --query "Password1" --ai-provider openai --ai-model gpt-5-mini
```

You can also use `--ai-api-key` or `--openai-api-key`.

## Rate Limits

GitHub code search has a small rate-limit bucket compared with many other GitHub APIs. GitRekt tries to reduce unnecessary API use by:

- sizing repository-scoped searches to the requested limit,
- caching fetched file contents,
- caching repository trees,
- using repository tree inspection in agent mode instead of repeated broad code searches,
- batching same-repository AI validation work,
- avoiding duplicate AI validation for identical file snippets,
- pacing requests when GitHub exposes rate-limit reset headers,
- refreshing GitHub App installation tokens during long runs.

For heavier use, prefer a GitHub App installed per customer or organization. Avoid running many customers through one shared GitHub credential.

## Exit Behavior

GitRekt exits non-zero for fatal setup or GitHub API errors, such as invalid credentials. Individual AI validation failures are printed next to the affected result so the scan can continue.

## Security Notes

- Do not commit GitHub App private keys or API keys.
- Prefer environment variables or secret stores for credentials.
- Review findings manually before rotating credentials or opening incidents.
- Some matches are intentionally reported as leads because surrounding repository context may matter.

## Releases

Releases are created automatically on pushes to the repository's default branch by the [Release workflow](https://github.com/Stratus-Security/GitRekt/actions/workflows/release.yml).

Each release contains NativeAOT builds for Windows, Linux, and macOS.
