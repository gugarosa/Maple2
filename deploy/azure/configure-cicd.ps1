#Requires -Version 5.1
<#
.SYNOPSIS
Provision MS2-only GitHub OIDC delivery access. Preview unless -Apply is supplied.
.DESCRIPTION
No client secret, public ingress, VM start/resize, source commit or deployment.
-Enable additionally requires the published workflow and configures protected PR merges.
#>
param(
    [Parameter(Mandatory = $true)][Guid]$SubscriptionId,
    [switch]$Apply,
    [switch]$Enable
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$subscription = $SubscriptionId.ToString()
if ($subscription -ne 'eb09d227-552f-4003-9129-c3f9cc36748d') { throw 'This configuration is restricted to the MS2 subscription.' }
if ($Enable -and -not $Apply) { throw '-Enable requires explicit -Apply.' }
$group = 'rg-maple2-brazilsouth'
$repository = 'gugarosa/Maple2'
$environment = 'maple2-pilot'
$identityName = 'id-maple2-github-deploy'
$roleName = 'Maple2 GitHub deployment command'
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    $result = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
    return $result
}
function Send-GitHubJson([string]$Endpoint, [string]$Method, [object]$Body) {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('maple2-gh-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        [IO.File]::WriteAllText($path, ($Body | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        $null = Invoke-Checked gh @('api', $Endpoint, '--method', $Method, '--input', $path)
    } finally { Remove-Item -LiteralPath $path }
}
$repo = Invoke-Checked gh @('api', "repos/$repository") | ConvertFrom-Json
if ($repo.default_branch -ne 'master' -or -not $repo.permissions.admin) { throw 'Expected admin access to the MS2 master repository.' }
$resourceGroup = Invoke-Checked az @('group', 'show', '--subscription', $subscription,
    '--name', $group, '--only-show-errors', '--output', 'json') | ConvertFrom-Json
if ($resourceGroup.tags.project -ne 'maple2' -or $resourceGroup.tags.application -ne 'private-pilot') {
    throw 'The target is not the isolated, application-owned MS2 pilot.'
}
$account = Invoke-Checked az @('account', 'show', '--subscription', $subscription, '--output', 'json') | ConvertFrom-Json
if ($account.tenantId -ne 'ffb26935-7e1d-4409-add6-17cf12ee143e') { throw 'Unexpected Azure tenant.' }
if (-not $Apply) {
    Write-Host 'Preview: create/reuse an MS2-only managed identity and GitHub environment federation.'
    Write-Host 'Grant RG read, private artifact-container contribution, and Run Command on the MS2 VM only.'
    Write-Host 'Provision non-secret environment IDs with automatic delivery initially disabled.'
    Write-Host 'No VM, budget, network, repository source or running application will be changed.'
    return
}
if ($Enable) {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    if (@(Invoke-Checked git @('-C', $root, 'status', '--porcelain')).Count -ne 0) {
        throw 'Enable delivery only from the clean, published and validated source baseline.'
    }
    $head = Invoke-Checked git @('-C', $root, 'rev-parse', 'HEAD')
    $remote = Invoke-Checked gh @('api', "repos/$repository/git/ref/heads/master") | ConvertFrom-Json
    if ($head -ne $remote.object.sha) { throw 'The activation checkout must match published master exactly.' }
    $workflows = Invoke-Checked gh @('api', "repos/$repository/actions/workflows") | ConvertFrom-Json
    if (@($workflows.workflows | Where-Object { $_.path -eq '.github/workflows/deploy.yml' -and $_.state -eq 'active' }).Count -ne 1) {
        throw 'Publish and validate the delivery workflow and its runtime baseline before enabling automatic deployment.'
    }
    $checks = Invoke-Checked gh @('api', "repos/$repository/commits/$head/check-runs?per_page=100") | ConvertFrom-Json
    foreach ($name in @('build', 'format', 'deployment-contracts')) {
        $latest = @($checks.check_runs | Where-Object {
            $_.name -match ('(^|/ )' + [regex]::Escape($name) + '$')
        } | Sort-Object id -Descending | Select-Object -First 1)
        if ($latest.Count -ne 1 -or $latest[0].status -ne 'completed' -or $latest[0].conclusion -ne 'success') {
            throw 'The published baseline must pass all CI checks before delivery is enabled.'
        }
    }
}
$identities = @(Invoke-Checked az @('identity', 'list', '--subscription', $subscription,
    '--resource-group', $group, '--output', 'json') | ConvertFrom-Json | ForEach-Object { $_ })
$identity = @($identities | Where-Object name -eq $identityName)
if ($identity.Count -eq 0) {
    $identity = @(Invoke-Checked az @('identity', 'create', '--subscription', $subscription,
        '--resource-group', $group, '--name', $identityName, '--location', $resourceGroup.location,
        '--tags', 'project=maple2', 'managedBy=github-cd', '--output', 'json') | ConvertFrom-Json | ForEach-Object { $_ })
}
if ($identity.Count -ne 1 -or $identity[0].tags.project -ne 'maple2') { throw 'Unexpected deployment identity ownership.' }
$identity = $identity[0]
$subject = "repo:${repository}:environment:$environment"
$credentials = @(Invoke-Checked az @('identity', 'federated-credential', 'list', '--subscription', $subscription,
    '--resource-group', $group, '--identity-name', $identityName, '--output', 'json') | ConvertFrom-Json | ForEach-Object { $_ })
if ($credentials.Count -eq 0) {
    $null = Invoke-Checked az @('identity', 'federated-credential', 'create', '--subscription', $subscription,
        '--resource-group', $group, '--identity-name', $identityName, '--name', 'github-maple2-pilot',
        '--issuer', 'https://token.actions.githubusercontent.com', '--subject', $subject,
        '--audiences', 'api://AzureADTokenExchange', '--output', 'none')
} elseif ($credentials.Count -ne 1 -or $credentials[0].subject -ne $subject -or
    $credentials[0].issuer -ne 'https://token.actions.githubusercontent.com' -or
    ($credentials[0].audiences -join ',') -ne 'api://AzureADTokenExchange') {
    throw 'Existing federation differs; refusing to broaden or replace it.'
}
$roles = @(Invoke-Checked az @('role', 'definition', 'list', '--subscription', $subscription,
    '--name', $roleName, '--output', 'json') | ConvertFrom-Json | ForEach-Object { $_ })
if ($roles.Count -eq 0) {
    $definition = @{
        Name = $roleName; IsCustom = $true
        Description = 'Execute approved MS2 delivery scripts on the existing MS2 VM; no VM lifecycle or network changes.'
        Actions = @('Microsoft.Compute/virtualMachines/runCommand/action')
        NotActions = @(); DataActions = @(); NotDataActions = @()
        AssignableScopes = @($resourceGroup.id)
    }
    $path = Join-Path ([IO.Path]::GetTempPath()) ('maple2-role-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        [IO.File]::WriteAllText($path, ($definition | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        $roles = @(Invoke-Checked az @('role', 'definition', 'create', '--subscription', $subscription,
            '--role-definition', "@$path", '--output', 'json') | ConvertFrom-Json | ForEach-Object { $_ })
    } finally { Remove-Item -LiteralPath $path }
}
if ($roles.Count -ne 1 -or ($roles[0].assignableScopes -join ',') -ne $resourceGroup.id -or
    $roles[0].permissions.Count -ne 1 -or
    ($roles[0].permissions[0].actions -join ',') -ne 'Microsoft.Compute/virtualMachines/runCommand/action' -or
    @($roles[0].permissions[0].dataActions).Count -ne 0) { throw 'Existing custom role exceeds the expected deployment scope.' }
$container = $resourceGroup.id + '/providers/Microsoft.Storage/storageAccounts/stmaple2lx7rwls5nb4z2/blobServices/default/containers/artifacts'
$vm = $resourceGroup.id + '/providers/Microsoft.Compute/virtualMachines/vm-maple2-brs'
foreach ($assignment in @(
    @{ Scope = $resourceGroup.id; Role = 'acdd72a7-3385-48ef-bd42-f606fba81ae7' },
    @{ Scope = $container; Role = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' },
    @{ Scope = $vm; Role = $roles[0].name }
)) {
    $null = Invoke-Checked az @('role', 'assignment', 'create', '--subscription', $subscription,
        '--assignee-object-id', $identity.principalId, '--assignee-principal-type', 'ServicePrincipal',
        '--role', $assignment.Role, '--scope', $assignment.Scope, '--output', 'none')
}
$environments = Invoke-Checked gh @('api', "repos/$repository/environments") | ConvertFrom-Json
$existing = @($environments.environments | Where-Object name -eq $environment)
if ($existing.Count -eq 0) {
    Send-GitHubJson "repos/$repository/environments/$environment" PUT @{
        deployment_branch_policy = @{ protected_branches = $false; custom_branch_policies = $true }
    }
} elseif ($existing.Count -ne 1 -or $null -eq $existing[0].deployment_branch_policy -or
    $existing[0].deployment_branch_policy.protected_branches -or
    -not $existing[0].deployment_branch_policy.custom_branch_policies) {
    throw 'Existing environment protections differ; refusing to replace them.'
}
$policies = Invoke-Checked gh @('api', "repos/$repository/environments/$environment/deployment-branch-policies") | ConvertFrom-Json
if ($policies.total_count -eq 0) {
    Send-GitHubJson "repos/$repository/environments/$environment/deployment-branch-policies" POST @{ name = 'master'; type = 'branch' }
} elseif ($policies.total_count -ne 1 -or $policies.branch_policies[0].name -ne 'master' -or
    $policies.branch_policies[0].type -ne 'branch') { throw 'Unexpected environment branch policy; review it manually.' }
foreach ($entry in @{
    AZURE_CLIENT_ID = $identity.clientId
    AZURE_TENANT_ID = $account.tenantId
    AZURE_SUBSCRIPTION_ID = $subscription
}.GetEnumerator()) {
    $null = Invoke-Checked gh @('variable', 'set', $entry.Key, '--repo', $repository,
        '--env', $environment, '--body', [string]$entry.Value)
}
if ($Enable) {
    $branch = Invoke-Checked gh @('api', "repos/$repository/branches/master") | ConvertFrom-Json
    if ($branch.protected) {
        $protection = Invoke-Checked gh @('api', "repos/$repository/branches/master/protection") | ConvertFrom-Json
        if (-not $protection.enforce_admins.enabled -or -not $protection.required_status_checks.strict -or
            $null -eq $protection.required_pull_request_reviews -or $protection.allow_force_pushes.enabled -or
            $protection.allow_deletions.enabled) { throw 'Existing branch protection needs an explicit safety review.' }
        foreach ($check in @('build', 'format', 'deployment-contracts')) {
            if ($check -notin $protection.required_status_checks.contexts) {
                throw 'Existing branch protection needs an explicit review; required CI checks are missing.'
            }
        }
    } else {
        Send-GitHubJson "repos/$repository/branches/master/protection" PUT @{
            required_status_checks = @{ strict = $true; contexts = @('build', 'format', 'deployment-contracts') }
            enforce_admins = $true
            required_pull_request_reviews = @{ required_approving_review_count = 0; dismiss_stale_reviews = $true }
            restrictions = $null
            allow_force_pushes = $false; allow_deletions = $false
        }
    }
    $null = Invoke-Checked gh @('variable', 'set', 'MS2_CD_ENABLED', '--repo', $repository, '--body', 'true')
} else {
    $variables = Invoke-Checked gh @('api', "repos/$repository/actions/variables") | ConvertFrom-Json
    if (@($variables.variables | Where-Object name -eq 'MS2_CD_ENABLED').Count -eq 0) {
        $null = Invoke-Checked gh @('variable', 'set', 'MS2_CD_ENABLED', '--repo', $repository, '--body', 'false')
    }
}
Write-Host "MS2 OIDC and master-only environment configured. Automatic delivery requested: $Enable"
Write-Host 'A successful hosted deployment is still required before claiming CI/CD is live.'
