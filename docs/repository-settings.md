# Repository settings for administrators

These settings need admin access on the Git host. The repository is hosted at
<https://github.com/msayco-fs/2-week-training-plan>. Branch protection on a
private repository needs GitHub Pro, Team or Enterprise.

## 1. Create the remote and push

```bash
git remote add origin https://github.com/msayco-fs/2-week-training-plan.git
git push -u origin <branch>
```

## 2. Let CI run once

Open a pull request (or push to `main` before protection is enabled) so GitHub
runs `.github/workflows/ci.yml`. GitHub only offers a check as "required" after
it has reported at least once. Make sure both jobs pass:

- `Backend (build and test)`
- `Frontend (lint, test, build)`

## 3. Protect `main`

GitHub: **Settings → Rules → Rulesets → New branch ruleset** (or
**Settings → Branches → Add classic branch protection rule**).

- Target: default branch (`main`). Enforcement: **Active**.
- **Restrict deletions**: on.
- **Block force pushes**: on.
- **Require a pull request before merging**: on.
  - Required approvals: **1** (or more).
  - Dismiss stale approvals when new commits are pushed: on.
  - Require review from Code Owners: optional (add a `CODEOWNERS` file first).
  - Require conversation resolution before merging: on.
- **Require status checks to pass**: on.
  - Require branches to be up to date before merging: on.
  - Required checks: `Backend (build and test)`, `Frontend (lint, test, build)`.
- Bypass list: empty. Also apply to administrators.

The same ruleset with the GitHub CLI (run as a repository admin):

```bash
gh api repos/<owner>/<repo>/rulesets --method POST --input - <<'JSON'
{
  "name": "Protect main",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "pull_request", "parameters": {
        "required_approving_review_count": 1,
        "dismiss_stale_reviews_on_push": true,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": true } },
    { "type": "required_status_checks", "parameters": {
        "strict_required_status_checks_policy": true,
        "required_status_checks": [
          { "context": "Backend (build and test)" },
          { "context": "Frontend (lint, test, build)" } ] } }
  ]
}
JSON
```

## 4. Other recommended settings

- **Settings → General → Pull Requests**: allow squash merging; turn on
  "Automatically delete head branches".
- **Settings → Actions → General**: workflow permissions "Read repository
  contents"; require approval for workflows from first-time contributors.
- **Settings → Code security**: turn on Dependabot alerts and secret scanning
  with push protection.
- Prefer a **private** repository, because it contains an intentional
  vulnerability. If the repository is public, never deploy the code, and
  keep the warning at the top of the README.

## 5. Verify

- Push a commit directly to `main`. It must be rejected.
- Open a pull request with a failing test. Merge must be blocked.
