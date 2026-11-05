# Billing Operations on Kubernetes

This .NET 10 service runs eleven billing workflows.
The image runs either an authenticated HTTP gateway or a single scheduled job,
following the role-based image, Azure Workload Identity, Key Vault CSI mount,
and Kubernetes manifests used by `Probono-Law-File-Operations`.

Licensed under the [Apache License 2.0](LICENSE).

| Logic App workflow | Kubernetes entry point | Behavior |
| --- | --- | --- |
| `AIMIE-stats-Retriever` (`AIMIE-Stats-Retriever` in source) | `aimie-stats-retriever` CronJob, every 30 minutes | Queries AIMIE dependency telemetry and dispatches QA and declaration events. |
| `AIMIE-Token-usage` (`AIMIE-Token-Usage`) | `aimie-token-usage` CronJob, hourly | Imports AIMIE token events into `accountUsage`. |
| `Cleanup-Usage-Tokens` | `cleanup-usage-tokens` CronJob, 03:00 Eastern on the 15th | Deletes billed usage with an invoice ID from before the current month. |
| `Generate-Declaration-Activity-Record` | `POST /v1/billing/activities/declaration` | Finds request activities, adjusts orchestration duration and status, and upserts `aimieStats`. |
| `Generate-QA-Activity-Record` | `POST /v1/billing/activities/qa` | Finds QA dependencies and upserts `aimieStats`. |
| `Get-Xero-Auth-Token` | `POST /v1/billing/xero/token` | Uses Xero client credentials from Key Vault, or injected settings. |
| `Get-Xero-Contact` | `POST /v1/billing/xero/contact` | Reads a Graph user, finds or creates a Xero contact by email. |
| `Ingestion-Token-Usage` | `ingestion-token-usage` CronJob, every 15 minutes | Imports ingestion token events into `accountUsage`. |
| `Invoice-Generate-Per-Account-Product-Line-Item` | `POST /v1/billing/invoices/line-items` | Resolves Xero items and token quantities. |
| `Invoice-Generate-Per-Account` | `POST /v1/billing/invoices/accounts` | Creates a draft with the base service and aggregated usage lines. |
| `Invoice-Generator` | `invoice-generator` CronJob, first day of month at midnight Eastern | Finds billable case accounts and invokes the per-account invoice flow. |

All HTTP routes except `/healthz` require `X-API-Key`. Keep the gateway private
behind an ingress or API gateway. CronJobs use the same image with a `ROLE` value
and exit after one run. The five CronJobs have `concurrencyPolicy: Forbid`; both
monthly jobs use `America/New_York` to retain the Logic Apps Eastern schedule.

The HTTP request bodies retain the Logic App field names. For example, a manual
account invoice accepts `{"oid":"...","email":"...","caseAccountId":"...","caseNumber":"...","billingCode":"...","billingMonth":"2026-09"}`.
`billingMonth` is optional. The contact route accepts `{"oid":"..."}`; the
token route accepts `{"scopes":[]}`. The activity routes accept `logId`,
`timestamp`, `accountId`, `sessionId`, `name`, `operationId`, `duration`,
`performanceBucket`, and `success`. The line-item route accepts `accountId`,
`model`, `llmType`, `origin`, token totals, `billingCode`, and
`billingCodeOnly`. Its response keeps the Logic App's `ItemCode`, `AccountCode`,
`UnitPrice`, `Quantity`, and `Description` names, with price and quantity as
strings.

## Billing behavior

The source `Invoice-Generator` stops after retrieving each account's contact and
usage; it never invokes `Invoice-Generate-Per-Account`. The source invoice flow
also never marks usage billed, so its cleanup flow has nothing to remove. This
service completes that chain: it creates a Xero **draft**, records the invoice
ID on included usage, and later removes only those billed records. A `billingRuns`
document with the account ID as partition key freezes a first-seen cutoff for each
account and billing month. On retry, the same snapshot and a deterministic Xero
reference/idempotency key are used. Usage arriving after the snapshot remains
unbilled and needs a separate adjustment or subsequent reconciliation; it is
never marked billed against the existing draft.

The recurring job bills the **previous complete UTC month**. The direct
per-account HTTP route defaults to the current UTC month, matching the legacy
per-account workflow; callers may pass `billingMonth` as `yyyy-MM` to select a
month explicitly. The source monthly trigger queries the current month, which
would be empty when it runs at the start of a month. Each draft includes one base
service line plus input and output token lines, or one embedding line. Quantities
are token counts divided by one million. Xero item codes follow the Logic App's
`-azure` and `-openai` provider suffixes. Repeated telemetry imports update the
same Cosmos document without clearing billing status.

The service relies on Xero's `Reference` lookup and `Idempotency-Key` to recover
from a process crash after a draft is accepted. Before live use, verify that the
connected Xero organization supports the expected OAuth scopes, item codes,
contact fields, tenant header, and reference query. It also requires a Test
environment run with real Graph, Azure Monitor, Cosmos, and Xero responses.

## Required Azure resources and settings

Copy [`appsettings.example.json`](appsettings.example.json) to a private JSON
file. Environment variables override the mounted file. Set `APPSETTINGS_FILE`
for local runs; in AKS the CSI driver mounts `/mnt/keyvault/appsettings.json`.

- `COSMOS_ENDPOINT` uses Workload Identity. `COSMOS_CONNECTION_STRING` can be used
  for local development instead. Set `COSMOS_DATABASE` to the target database.
  `accountUsage`, `aimieStats`, and `billingRuns` must
  have `/accountId` as their partition key, except `aimieStats`, which uses
  `/accountId` and `/name` in both Test and Production. The existing `accounts` container
  must contain the root account document used by the source query.
- `AIMIE_APP_INSIGHTS_APP_ID` and `INGESTION_APP_INSIGHTS_APP_ID` are the
  **Application Insights application IDs** (API Access IDs), not Log Analytics
  workspace IDs. The pod identity needs Reader access to both Application
  Insights resources. The service uses the Application Insights query API with
  Workload Identity so the Logic Apps' `dependencies`, `customEvents`, and
  `requests` KQL retains its original table and column names.
- `BILLING_KEY_VAULT_URI` points to the vault with `XeroClientID` and
  `XeroClientSecret`. As an alternative, inject `XERO_CLIENT_ID` and
  `XERO_CLIENT_SECRET` directly through the private appsettings file. Set
  `XERO_TENANT_ID` if the Xero organization requires the tenant header and
  `XERO_SCOPES` if its custom connection requires explicit scopes. When
  `XERO_SCOPES` is blank, the token request omits the `scope` field so Xero can
  use the connection's default scopes.
- The pod identity needs Microsoft Graph application permission to read users.
  Alternatively set `GRAPH_TENANT_ID`, `GRAPH_CLIENT_ID`, and
  `GRAPH_CLIENT_SECRET` for a separate Graph app registration.
- The AKS workload identity needs Key Vault Secrets User on the mounted settings
  secret and Reader access to both Application Insights resources. The Test
  settings file supplies the existing Cosmos connection string and separate
  Graph and Xero application credentials. The federation subject is
  `system:serviceaccount:billing-operations:billing-operations`.

Create the `billingRuns` Cosmos container before starting the invoice job.
Its partition key is `/accountId`. The existing `accountUsage` and `aimieStats`
containers must use the same key, or their adapter settings/code must be
adjusted to match the deployed containers.

## Build and deploy

### VS Code local debugging

Copy the settings template to the ignored Azure Test file and fill in Test
resource values:

```sh
cp appsettings.example.json .vscode/azure-test.appsettings.json
az login --tenant "<azure-test-tenant-id>"
```

The launch profiles in [`.vscode/launch.json`](.vscode/launch.json) build the
Debug project, use that settings file, and authenticate to Azure through the
CLI session. Select **Billing Operations: Gateway (Azure Test)** to serve HTTP
at `http://localhost:5080`; the other profiles run their scheduled role once.
The Invoice Generator profile creates Xero drafts, and Cleanup Usage Tokens
deletes billed usage in the configured Test resources.

### Container and Kubernetes

```sh
dotnet test tests/BillingOperations.Tests/BillingOperations.Tests.csproj -c Release
docker build -t billing-operations:<immutable-tag> .
```

The Test settings file is stored in a private Key Vault secret named
`billing-operations-appsettings`.
The `Container image` and `Deploy AKS Test` workflows read Azure values from
the GitHub `test` environment variables. Set `AZURE_CLIENT_ID` and
`AKS_DEPLOY_CLIENT_ID` to the Test GitHub deployment identity; set
`AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, and
`AZURE_WORKLOAD_IDENTITY_CLIENT_ID` to their Test IDs. Set
`AKS_RESOURCE_GROUP`, `AKS_CLUSTER_NAME`, `KEY_VAULT_NAME`, `ACR_NAME`, and
`ACR_LOGIN_SERVER` in that environment. The
deployment script renders the identity, vault and immutable image tag into
temporary manifests, then applies them through AKS Run Command.

For a manual deployment, export the same values in your shell and run:

```sh
IMAGE_REPOSITORY="$ACR_LOGIN_SERVER/billing-operations" \
IMAGE_TAG="<immutable-tag>" \
bash .github/scripts/deploy-aks.sh
```

### Production AKS deployment

The [production workflow](.github/workflows/deploy-aks-production.yml) builds
and publishes an immutable image to the production registry, then applies the
gateway and all five CronJobs to the production AKS cluster. The deploy script
checks the cluster resource ID and passes the subscription explicitly to every
AKS command. It never uses the Test cluster or registry settings.

The workflow reads the subscription, tenant, AKS cluster, registry, vault,
identities, gateway replica count, and invoice schedule setting from GitHub's
`production` environment variables. Set `AKS_DEPLOY_CLIENT_ID`,
`AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`,
`AZURE_WORKLOAD_IDENTITY_CLIENT_ID`, `AKS_RESOURCE_GROUP`, `AKS_CLUSTER_NAME`,
`KEY_VAULT_NAME`, `ACR_NAME`, `ACR_LOGIN_SERVER`, `GATEWAY_REPLICAS`, and
`INVOICE_GENERATOR_SUSPEND` there. The deploy identity is
federated with the GitHub `production` environment and has push permission on
the production ACR and Run Command permission on the production AKS cluster.
The pod identity is federated with
`system:serviceaccount:billing-operations:billing-operations` and has access
to the production settings and Xero vaults, Application Insights resources,
and Cosmos database. It also has Microsoft Graph `User.Read.All` application
permission for contact lookups.

The production settings secret is `billing-operations-appsettings` in the
production Key Vault. Use
[the production template](appsettings.production.example.json) when updating
it. Its `API_KEY` is generated and stored only in Key Vault. The production
`aimieStats` container has a hierarchical `/accountId`, `/name` key; the
adapter writes with that key in both Test and Production.

Monitor Xero API calls and billing data as the schedules run. The gateway
Service is private (`ClusterIP`); configure an ingress or API gateway before
routing external callers. No missing credential is represented by a literal
placeholder in the live Key Vault secret.

The production workflow runs only when manually dispatched from `main`.
Test and Production share the
[`CronJob` manifest](k8s/billing-operations/cronjobs.yaml). The deployment
script keeps `invoice-generator` suspended in Test and enables it in Production;
the other four schedules run in both environments.

`invoice-generator` remains suspended in Test until its Xero and Cosmos data
have been validated using the
[catalog check](docs/test-xero-catalog-gaps.md). HTTP callers
must be repointed to the gateway routes and supplied the API key.

The monthly cleanup job drains all matching records in batches, improving on
the Logic App's single `TOP 1000` pass. If a Xero request fails, the CronJob
fails and Kubernetes retries it; completed account runs are recovered through
`billingRuns` and Xero's reference lookup.

## Public release

Before publishing `ProBonoLaw/PBL-Billing-Operations`, follow the
[release checklist](docs/public-release.md). After configuration, changes to
public `main` request a sync from a protected private control branch. That
workflow fetches the public commits and automatically merges them into private
`main`. Private history is preserved; a merge conflict fails the sync for
resolution in the private repository. Azure deployment jobs run only in private
repositories.

## Telemetry

The service emits structured JSON logs to stdout for AKS log collection. Each
HTTP response includes `X-Request-ID`; the same `RequestId` and `TraceId` appear
in the request's log scopes. Each CronJob execution gets a `RunId` and `Role`.
Invoice steps also include `BillingMonth` and a short hash in `AccountRef` so
one account's steps can be followed without logging its identifier.

Logs identify request start and completion, Application Insights queries and
row counts, Cosmos read and write stages, Graph and Xero calls, invoice draft
creation or recovery, billing status updates, and cleanup counts. Credentials,
access tokens, request bodies, and personal account fields are not logged.

To follow one request or CronJob run in the Test Log Analytics workspace, use
its `X-Request-ID` or `RunId`:

```kusto
ContainerLogV2
| where PodNamespace == "billing-operations"
| where LogMessage contains "<request-id-or-run-id>"
| extend entry = parse_json(LogMessage)
| project TimeGenerated, PodName, Message = tostring(entry.Message), State = entry.State
| order by TimeGenerated asc
```
