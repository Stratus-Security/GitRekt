# GitRekt

GitRekt helps you triage GitHub code-search results for exposed secrets, credentials, sensitive configuration, and high-risk personal data across public or authorized repositories.

GitHub code search is powerful, but raw results are noisy: the same snippet can appear in many generated files, old exports, backups, fixtures, docs, or false-positive examples. GitRekt adds the workflow layer around search: direct file links, line resolution, duplicate reduction, rate-limit handling, optional GitHub App auth, and optional AI review that can explain why a match is worth looking at.

> GitRekt is a triage tool. Treat findings as leads to review, not proof that a secret is valid or exploitable.

<img src="demo.gif" alt="GitRekt demo" width="1440">
The above example is a useful use case when penetration testing. Simply search the customers domain (e.g. @domain.com) with AI and agent mode enabled for best results as seen here. This often finds valid credentials or leaked data for individual companies.

## When GitRekt Helps

Use GitRekt when you already have a signal you want to investigate across GitHub:

- leaked-looking passwords, tokens, API keys, client secrets, private keys, or connection strings,
- company domains, internal hostnames, product names, customer names, or project codenames,
- backup files, config files, `.env` files, CSV exports, logs, or migration dumps,
- broad PII searches where you need to separate useful findings from ordinary public contact data,
- periodic checks for accidental exposure across your own repositories or repositories you are authorized to review.

## How It Works

GitRekt searches GitHub gists and repositories, streams matches as they are found, and prints readable results with direct GitHub links. By default it searches `gists,repos` in that order. Gists are discovered through GitHub's gist search results, then GitRekt fetches matching gist files for snippets, line anchors, and AI context. When AI validation is enabled, each result is classified as `likely`, `possible`, or `none` so you can filter out obvious noise.

Agent mode goes further: before classifying a match, GitRekt gathers repository context such as the matched file, high-signal companion files, and suspicious paths from the repository tree. This helps catch cases where the first match is only a clue, but a nearby `.env`, config backup, CSV export, or token-bearing file is the real issue.

## What It Is Not

GitRekt does not validate whether a credential still works, exploit findings, or replace secret-scanning in CI. It is best used as a discovery and triage layer for researchers, security teams, and maintainers who need to review GitHub search results quickly and consistently.

## Download

Download prebuilt binaries from the [GitHub Releases page](https://github.com/Stratus-Security/GitRekt/releases).

After extracting on Linux or macOS, make the binary executable if needed:

```bash
chmod +x GitRekt
```

## Basic Usage

Search for a simple string:

```bash
GitRekt --query "Password1"
```

Search for multiple terms in one run:

```bash
GitRekt --query "Password1" --query "Password2" --query "@example.com"
```

Search only repositories:

```bash
GitRekt --query "Password1" --sources repos
```

Choose ordered sources explicitly:

```bash
GitRekt --query "Password1" --sources repos,gists
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

A fine-grained personal access token is the simplest option for individual use. Create one from [GitHub's personal access token settings](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/creating-a-personal-access-token):

1. Go to GitHub: **Settings** -> **Developer settings** -> **Personal access tokens** -> **Fine-grained tokens**.
2. Click **Generate new token**.
3. Choose the owner and repositories GitRekt should be allowed to search.
4. Set repository permissions:
   - **Contents**: **Read-only**
   - **Metadata**: **Read-only** if GitHub shows it as configurable; GitHub often includes metadata access automatically.
5. Generate the token and store it somewhere safe. GitHub only shows it once.

Set an environment variable:

```bash
export GITHUB_ACCESS_TOKEN="github_pat_..."
GitRekt --query "Password1"
```

Or pass it directly:

```bash
GitRekt --token "github_pat_..." --query "Password1"
```

If you must use a classic token, use the smallest scope that works for your target repositories. Private repository searches generally require the broader `repo` scope.

### GitHub App

GitHub App auth is a better fit for longer scans because installation tokens can be refreshed and scoped to the installed account.

Create the app from [GitHub's GitHub App registration page](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app):

1. Go to GitHub: **Settings** -> **Developer settings** -> **GitHub Apps** -> **New GitHub App**. For an organization-owned app, use the organization's settings instead.
2. Give it a clear name, such as `GitRekt Scanner`.
3. Set **Homepage URL** to your project, company, or repository URL.
4. Disable **Active** webhooks unless you need them for something else. GitRekt does not need webhooks.
5. Set repository permissions:
   - **Contents**: **Read-only**
   - **Metadata**: **Read-only**
6. Do not subscribe to webhook events.
7. Choose where the app can be installed:
   - **Only on this account** for personal/internal use.
   - **Any account** if other organizations should install it.
8. Click **Create GitHub App**.
9. Copy the **App ID** from the app's **General** page.
10. Under **Private keys**, click **Generate a private key** and download the `.pem` file.
11. Click **Install App** and install it on the account or repositories GitRekt should scan.

If the app has multiple installations, copy the installation ID from the installed app URL. It is the numeric ID in a URL like `https://github.com/settings/installations/12345678`.

Set:

```bash
export GITHUB_APP_ID="12345"
export GITHUB_APP_INSTALLATION_ID="67890"
export GITHUB_APP_PRIVATE_KEY_PATH="/path/to/app.private-key.pem"

GitRekt --query "Password1"
```

If the app has exactly one installation, `GITHUB_APP_INSTALLATION_ID` is optional. GitRekt can also pick up exactly one `*.private-key.pem` file from the current directory when `GITHUB_APP_ID` is set.

GitHub App installation tokens are short-lived; GitRekt creates and refreshes them from the app ID, installation ID, and private key. The app does not bypass GitHub code-search rate limits, but it gives cleaner per-installation scoping and avoids long-lived user credentials.

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

- `likely`
- `possible`
- `none`

You can filter the output by verdict, which includes more sensitive verdicts too:

```bash
GitRekt --query "Password1" --ai --ai-model llama3.2 --ai-verdict possible
```
This example command shows sensitive and potentially sensitive results but hides any that aren't considered sensitive by the AI.

Use strict mode when broad PII-style searches produce too many ordinary business contact matches:

```bash
GitRekt --query "@example.com" --ai --strict --ai-model llama3.2 --ai-verdict possible
```

Strict mode treats marketing lists, public staff directories, and ordinary work contact details such as name, company, email, job title, and office phone as low signal. It still keeps higher-impact findings such as credentials, tokens, private keys, home addresses, government IDs, dates of birth, salary or compensation data, financial data, medical data, personal account data, and private customer records.

### Agent Mode

Agent mode gathers same-repository context before validation. It works with every AI by adding matched file excerpts and high-signal repository candidates before asking the model to classify the result.
The agent also looks for other sensitive files within the repo, automagically finding secrets, PII, etc that may be leaked relating to a matching keyword. For gist results, agent mode is limited to the matched gist and other files in that same gist.

> Note: This mode uses more tokens, plain AI mode simply classifies the context from GitHub search.

```bash
GitRekt --query "Password1" --ai-agent --ai-model llama3.2 --ai-verdict possible
```

### Ollama

Ollama is the default AI provider.

```bash
GitRekt --query "Password1" --ai --ai-model llama3.2
```

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

## Acknowledgements

GitRekt was inspired in part by Bishop Fox's [GitGot](https://github.com/BishopFox/GitGot), a long-standing GitHub secret-search tool.
