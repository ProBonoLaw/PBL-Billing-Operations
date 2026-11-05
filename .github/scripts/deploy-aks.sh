#!/usr/bin/env bash
set -euo pipefail

: "${AZURE_TENANT_ID:?Set AZURE_TENANT_ID}"
: "${AZURE_WORKLOAD_IDENTITY_CLIENT_ID:?Set AZURE_WORKLOAD_IDENTITY_CLIENT_ID}"
: "${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}"
: "${AKS_RESOURCE_GROUP:?Set AKS_RESOURCE_GROUP}"
: "${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME}"
: "${KEY_VAULT_NAME:?Set KEY_VAULT_NAME}"
: "${IMAGE_REPOSITORY:?Set IMAGE_REPOSITORY}"
: "${IMAGE_TAG:?Set IMAGE_TAG}"
GATEWAY_REPLICAS=${GATEWAY_REPLICAS:-2}
INVOICE_GENERATOR_SUSPEND=${INVOICE_GENERATOR_SUSPEND:-true}

if [[ "$IMAGE_TAG" =~ ^[[:xdigit:]]{40}$ ]]; then
  IMAGE_TAG="sha-${IMAGE_TAG}"
fi

guid_pattern='^[[:xdigit:]]{8}(-[[:xdigit:]]{4}){3}-[[:xdigit:]]{12}$'
if [[ ! "$AZURE_TENANT_ID" =~ $guid_pattern || ! "$AZURE_WORKLOAD_IDENTITY_CLIENT_ID" =~ $guid_pattern || ! "$AZURE_SUBSCRIPTION_ID" =~ $guid_pattern ]]; then
  echo "Azure subscription, tenant, and workload identity client IDs must be GUIDs" >&2
  exit 1
fi
if [[ ! "$IMAGE_REPOSITORY" =~ ^[a-z0-9][a-z0-9.-]*\.azurecr\.io/billing-operations$ ]]; then
  echo "IMAGE_REPOSITORY must be an Azure Container Registry billing-operations repository" >&2
  exit 1
fi
if [[ ${DEPLOY_ENVIRONMENT:-test} != test && ${DEPLOY_ENVIRONMENT:-test} != production ]]; then
  echo "DEPLOY_ENVIRONMENT must be test or production" >&2
  exit 1
fi
if [[ ! "$GATEWAY_REPLICAS" =~ ^[0-9]+$ ]]; then
  echo "GATEWAY_REPLICAS must be a nonnegative integer" >&2
  exit 1
fi
if [[ "$INVOICE_GENERATOR_SUSPEND" != true && "$INVOICE_GENERATOR_SUSPEND" != false ]]; then
  echo "INVOICE_GENERATOR_SUSPEND must be true or false" >&2
  exit 1
fi
if [[ ! "$KEY_VAULT_NAME" =~ ^[A-Za-z0-9-]{3,24}$ || ! "$IMAGE_TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]]; then
  echo "Invalid Key Vault name or image tag" >&2
  exit 1
fi

rendered_dir=$(mktemp -d)
trap 'rm -r -- "$rendered_dir"' EXIT
cp -R k8s/billing-operations "$rendered_dir/"

for manifest in "$rendered_dir"/billing-operations/*.yaml; do
  sed -i \
    -e "s@<workload-identity-client-id>@${AZURE_WORKLOAD_IDENTITY_CLIENT_ID}@g" \
    -e "s@<key-vault-name>@${KEY_VAULT_NAME}@g" \
    -e "s@<azure-tenant-id>@${AZURE_TENANT_ID}@g" \
    -e "s@<image-repository>@${IMAGE_REPOSITORY}@g" \
    -e "s@<image-tag>@${IMAGE_TAG}@g" \
    -e "s@<gateway-replicas>@${GATEWAY_REPLICAS}@g" \
    -e "s@<invoice-generator-suspend>@${INVOICE_GENERATOR_SUSPEND}@g" \
    "$manifest"
done

bundle="$rendered_dir/billing-operations.yaml"
first=true
for name in namespace service-account appsettings-secretproviderclass gateway cronjobs; do
  if [[ "$first" == false ]]; then printf '\n---\n' >> "$bundle"; fi
  cat "$rendered_dir/billing-operations/$name.yaml" >> "$bundle"
  first=false
done

if [[ ${BILLING_DIRECT_KUBECTL:-0} == 1 ]]; then
  kubectl apply -f "$bundle"
  if (( GATEWAY_REPLICAS > 0 )); then
    kubectl rollout status deployment/billing-operations-gateway \
      --namespace billing-operations --timeout=4m
  fi
  exit 0
fi

cluster_id=$(az aks show \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$AKS_RESOURCE_GROUP" \
  --name "$AKS_CLUSTER_NAME" \
  --query id --output tsv)
expected_cluster_id="/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AKS_RESOURCE_GROUP}/providers/Microsoft.ContainerService/managedClusters/${AKS_CLUSTER_NAME}"
if [[ "${cluster_id,,}" != "${expected_cluster_id,,}" ]]; then
  echo "AKS cluster ID did not match the requested subscription, resource group, and name" >&2
  exit 1
fi
echo "Deploying to AKS cluster $cluster_id"

remote_command='kubectl apply -f billing-operations.yaml'
if (( GATEWAY_REPLICAS > 0 )); then
  remote_command+=' && kubectl rollout status deployment/billing-operations-gateway -n billing-operations --timeout=4m'
fi

if ! invoke_output=$(az aks command invoke \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$AKS_RESOURCE_GROUP" \
  --name "$AKS_CLUSTER_NAME" \
  --command "$remote_command" \
  --file "$bundle" \
  --no-wait 2>&1); then
  printf '%s\n' "$invoke_output" >&2
  echo "Could not submit the AKS Run Command" >&2
  exit 1
fi
printf '%s\n' "$invoke_output"

command_id=$(printf '%s\n' "$invoke_output" | sed -nE 's/.*command id: ([[:xdigit:]]+).*/\1/Ip' | head -n1)
if [[ -z "$command_id" ]]; then
  echo "AKS Run Command did not return a command ID" >&2
  exit 1
fi

poll_started_at=$(date +%s)
while true; do
  if ! result=$(az aks command result \
    --subscription "$AZURE_SUBSCRIPTION_ID" \
    --resource-group "$AKS_RESOURCE_GROUP" \
    --name "$AKS_CLUSTER_NAME" \
    --command-id "$command_id" \
    --output json 2>&1); then
    elapsed_seconds=$(( $(date +%s) - poll_started_at ))
    if (( elapsed_seconds < 60 )); then
      sleep 5
      continue
    fi
    printf '%s\n' "$result" >&2
    echo "Could not retrieve AKS Run Command result $command_id" >&2
    exit 1
  fi

  if result_json=$(printf '%s\n' "$result" | jq -ce 'select(type == "object")' 2>/dev/null); then
    state=$(printf '%s\n' "$result_json" | jq -r '.provisioningState // empty')
    if [[ "$state" == Succeeded || "$state" == Failed ]]; then
      result=$result_json
      break
    fi
  else
    state=$(printf '%s\n' "$result" | sed -nE 's/^command id: [[:xdigit:]]+, started at: .*status: ([[:alpha:]]+).*/\1/p')
    if [[ -z "$state" ]]; then
      printf '%s\n' "$result" >&2
      echo "Could not parse AKS Run Command status for $command_id" >&2
      exit 1
    fi
  fi

  elapsed_seconds=$(( $(date +%s) - poll_started_at ))
  if (( elapsed_seconds >= 600 )); then
    echo "Timed out waiting for AKS Run Command $command_id (state: ${state:-unknown})" >&2
    exit 1
  fi
  sleep 5
done

printf '%s\n' "$result" | jq -r '.logs // empty'
exit_code=$(printf '%s\n' "$result" | jq -r '.exitCode // 1')
if [[ "$state" != Succeeded || "$exit_code" != 0 ]]; then
  reason=$(printf '%s\n' "$result" | jq -r '.reason // empty')
  echo "AKS Run Command failed (state: $state, exit code: $exit_code${reason:+, reason: $reason})" >&2
  exit 1
fi
