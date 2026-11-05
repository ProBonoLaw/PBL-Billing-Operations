# Public release checklist

Public development happens in `ProBonoLaw/PBL-Billing-Operations`. After a push
or merge to its `main`, the public workflow requests a private integration run.
That run executes from a protected private control branch, fetches public
`main`, and merges it into private `main`, preserving private commits. Azure
publishing and deployments run only in private repositories.

Automatic integration treats reviewed public `main` as authorized private
deployment code, including its workflow and build files. Repository visibility
is not an approval boundary. Public merge approval must therefore be controlled
by the same trusted operators who authorize private deployments. If public
maintainers are not trusted for that role, keep automatic integration disabled
and require private review instead. The protected control branch isolates the
integration credential; it does not make arbitrary public code safe to deploy.

1. Create `ProBonoLaw/PBL-Billing-Operations` as a **new repository with a new
   root commit** containing only the reviewed working tree. Do not transfer,
   fork, mirror, or push this repository's existing Git history or branches to
   the public repository. Older commits contain internal Azure IDs, resource
   names, and a deleted deployment script.
   Commit the snapshot using an approved public author identity. Audit the new
   repository after publishing, including its branches, tags, releases, and
   workflow artifacts.
2. Include the approved [Apache License 2.0](../LICENSE) in the public
   snapshot and review its copyright attribution with the rights holder.
3. Configure the `test` and `production` GitHub environments in the private
   repository using the settings in the README. Keep Azure identities,
   resource identifiers, credentials, and Key Vault settings there. Azure OIDC
   federation continues to reference the private repository. GitHub variables
   are for nonsecret configuration; credentials belong in secrets or Key Vault.
4. Grant public write/admin access only to trusted private deployment operators;
   outside contributors submit pull requests without write access. Protect
   public `main` with required approval from those operators, passing CI, stale
   approval dismissal, approval of the latest push, and no force pushes or rule
   bypass. Apply that review to all code, workflow, build, and dependency changes.
   Before enabling sync, verify that an outside contributor cannot merge or
   dispatch privileged workflows and that an unapproved change cannot reach
   `main`. This approval authorizes the subsequent private execution without
   another private pull request. Configure private branch rules to allow the
   merge App to push `main`.
   A rule requiring a private pull request for every change will block this
   automatic integration unless it permits the App. Restrict production
   deployment dispatch and environment approval to the private operators.
5. Use a fresh clone of the public repository for future development. Its
   `origin` remote will point to the public repository, so ordinary pushes and
   pull requests go there. Do not reuse a clone whose `origin` still points to
   the private repository.
6. Create two separate GitHub Apps, installed only on the private upstream
   repository. Give the notification App **Actions: write**, with no Contents
   or Workflows permission. Give the merge App **Contents: write** and
   **Workflows: write**, the latter to copy workflow file changes. Store the
   keys only in their respective environments below. The private merge App's
   push triggers existing private CI; an ordinary `GITHUB_TOKEN` push would
   suppress those downstream workflows.
7. In the private repository, create `private-sync-control` from the reviewed
   commit containing the integration workflow and script. Protect that branch
   with private maintainer review and exclude both Apps from any bypass or
   direct update permission on it. Restrict the `public-main-integration`
   environment to **only the branch `private-sync-control`**, with no allowed
   tags. Keep the merge App key exclusively in this environment, never as a
   repository secret. Public changes to a copied workflow or script must not
   grant access to these credentials. Updates to the control branch require
   a separate private review. Retain the dispatch workflow on private default
   `main` so GitHub can find it, but dispatch and check out the control branch.
   Configure both environments before the first public push and set the private
   repository variable `ENABLE_PUBLIC_MAIN_SYNC=true` only after verifying the
   public approval controls and private environment restrictions.
8. For the first sync, export the reviewed private `main` tree exactly into the
   new public root commit. When both trees match, the private workflow links
   the two histories automatically. If they differ, it fails and requires a
   reviewed initial integration in the private repository. Later syncs use
   normal merges. Conflicts, rejected pushes, and missing credentials fail the
   run; private commits are never force overwritten. Resolve a conflict in the
   private repository and retry **Integrate public main into private main**
   from `private-sync-control`. The public request workflow also supports a
   manual retry from public `main`.
9. Verify the full path by merging a reviewed change into public `main` and
   confirming that its commit is an ancestor of private `main`. The private
   merge commit has its own SHA. Include an application and workflow file
   update to verify the App's workflow permission and the resulting private CI
   run. A successful public request means the private run was requested; check
   the private run's result to establish that the merge and push succeeded.

| Repository / environment | GitHub variables | GitHub secrets |
| --- | --- | --- |
| Public / `private-upstream-sync` | `SYNC_NOTIFY_APP_ID` | `UPSTREAM_OWNER`, `UPSTREAM_REPOSITORY`, `SYNC_NOTIFY_APP_PRIVATE_KEY` |
| Private / `public-main-integration` | `SYNC_MERGE_APP_ID` | `SYNC_MERGE_APP_PRIVATE_KEY` |

The destination owner and repository name are secrets so GitHub masks them in
public action logs. The [public request](../.github/workflows/sync-private-upstream.yml)
does not receive the merge App key or fetch private history. The
[private integration](../.github/workflows/integrate-public-main.yml) includes
all commits reachable from public `main`; feature branch changes are included
when merged, and tags are not copied. Queued and retried runs read the current
branch tips. Private history never flows back to the public repository.

No SharePoint integration or organization-specific SharePoint value was found
in the current tree or reachable history. If a SharePoint integration is added,
supply nonsecret site URLs, site IDs, list IDs, and similar values through
GitHub variables such as `SHAREPOINT_SITE_URL` and `SHAREPOINT_LIST_ID`.
Supply authentication material through GitHub secrets or Key Vault. Do not
commit organization-specific SharePoint values in examples, workflows, source,
or documentation.
