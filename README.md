# Learn Azure AD On-Behalf-Of (OBO) Flow with .NET

This solution demonstrates the **AAD OBO flow** end-to-end with 3 projects and 3 Azure AD app registrations.

## Architecture

```
┌─────────────┐       token A        ┌─────────────────┐     OBO → token B     ┌────────────────┐
│  ClientApp   │ ──────────────────►  │  MiddleTierApi  │ ───────────────────►  │  DownstreamApi  │
│  (Console)   │  scope: middle-tier  │  (Web API)      │  scope: downstream    │  (Web API)      │
│              │                      │  Port 7213      │                       │  Port 7022      │
└─────────────┘                      └─────────────────┘                       └────────────────┘
      │                                      │                                        │
  Interactive                          OBO Exchange:                            Validates token B
  browser login                    token A → token B                          (issued for this API)
```

### How OBO Works
1. **User signs in** via the ClientApp (interactive browser popup)
2. ClientApp receives **Token A** scoped to `api://MiddleTierApi/access_as_user`
3. ClientApp calls **MiddleTierApi** with Token A in the `Authorization` header
4. MiddleTierApi sends Token A to Azure AD and says: *"exchange this for a new token scoped to DownstreamApi, on behalf of this user"*
5. Azure AD returns **Token B** scoped to `api://DownstreamApi/.default`
6. MiddleTierApi calls **DownstreamApi** with Token B
7. DownstreamApi validates Token B and returns data

---

## Step 1: Azure AD App Registrations

You need to register **3 apps** in [Azure Portal → Microsoft Entra ID → App registrations](https://portal.azure.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/RegisteredApps).

### 1A. Register: DownstreamApi

1. Click **New registration**
2. Name: `LearnOBO-DownstreamApi`
3. Supported account types: **Single tenant**
4. Click **Register**
5. Go to **Expose an API**:
   - Click **Set** next to Application ID URI → accept `api://<client-id>` or customize
   - Click **Add a scope**:
     - Scope name: `access_as_user`
     - Who can consent: **Admins and users**
     - Admin consent display name: `Access DownstreamApi as user`
     - Click **Add scope**
6. Note down:
   - **Application (client) ID** → `YOUR_DOWNSTREAM_API_CLIENT_ID`
   - **Directory (tenant) ID** → `YOUR_TENANT_ID`

### 1B. Register: MiddleTierApi

1. Click **New registration**
2. Name: `LearnOBO-MiddleTierApi`
3. Supported account types: **Single tenant**
4. Click **Register**
5. Go to **Expose an API**:
   - Click **Set** next to Application ID URI → accept `api://<client-id>`
   - Click **Add a scope**:
     - Scope name: `access_as_user`
     - Who can consent: **Admins and users**
     - Admin consent display name: `Access MiddleTierApi as user`
     - Click **Add scope**
6. Go to **Certificates & secrets**:
   - Click **New client secret**
   - Description: `OBO-Secret`
   - Click **Add**
   - ⚠️ **Copy the secret value now** (you won't see it again) → `YOUR_MIDDLETIER_API_CLIENT_SECRET`
7. Go to **API permissions**:
   - Click **Add a permission** → **My APIs** → select `LearnOBO-DownstreamApi`
   - Select **Delegated permissions** → check `access_as_user`
   - Click **Add permissions**
   - Click **Grant admin consent** (if you have admin rights)
8. Note down:
   - **Application (client) ID** → `YOUR_MIDDLETIER_API_CLIENT_ID`

### 1C. Register: ClientApp

1. Click **New registration**
2. Name: `LearnOBO-ClientApp`
3. Supported account types: **Single tenant**
4. Redirect URI: Platform = **Mobile and desktop applications** → `http://localhost`
5. Click **Register**
6. Go to **API permissions**:
   - Click **Add a permission** → **My APIs** → select `LearnOBO-MiddleTierApi`
   - Select **Delegated permissions** → check `access_as_user`
   - Click **Add permissions**
   - Click **Grant admin consent** (if you have admin rights)
7. Note down:
   - **Application (client) ID** → `YOUR_CLIENT_APP_CLIENT_ID`

---

## Step 2: Configure the Projects

Replace the placeholder values in each project's config files:

### DownstreamApi/appsettings.json
```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "<YOUR_TENANT_ID>",
    "ClientId": "<YOUR_DOWNSTREAM_API_CLIENT_ID>",
    "Audience": "api://<YOUR_DOWNSTREAM_API_CLIENT_ID>"
  }
}
```

### MiddleTierApi/appsettings.json
```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "<YOUR_TENANT_ID>",
    "ClientId": "<YOUR_MIDDLETIER_API_CLIENT_ID>",
    "ClientSecret": "<YOUR_MIDDLETIER_API_CLIENT_SECRET>",
    "Audience": "api://<YOUR_MIDDLETIER_API_CLIENT_ID>"
  },
  "DownstreamApi": {
    "BaseUrl": "https://localhost:7022",
    "Scopes": [ "api://<YOUR_DOWNSTREAM_API_CLIENT_ID>/.default" ]
  }
}
```

### ClientApp/Program.cs (constants at the top)
```csharp
const string tenantId = "<YOUR_TENANT_ID>";
const string clientAppClientId = "<YOUR_CLIENT_APP_CLIENT_ID>";
const string middleTierApiScope = "api://<YOUR_MIDDLETIER_API_CLIENT_ID>/access_as_user";
```

---

## Step 3: Run

Open **3 terminals** in the solution root:

```bash
# Terminal 1 — Start DownstreamApi (port 7022)
dotnet run --project DownstreamApi --launch-profile https

# Terminal 2 — Start MiddleTierApi (port 7213)
dotnet run --project MiddleTierApi --launch-profile https

# Terminal 3 — Run ClientApp
dotnet run --project ClientApp
```

The ClientApp will:
1. Open your browser for interactive login
2. Acquire a token scoped to the MiddleTierApi
3. Call `GET /api/weather-aggregator` on the MiddleTierApi
4. The MiddleTierApi will perform OBO and call the DownstreamApi
5. You'll see the aggregated response in the console

---

## Key Files to Study

| File | What to learn |
|------|--------------|
| `ClientApp/Program.cs` | How to use MSAL to acquire tokens interactively and call a protected API |
| `MiddleTierApi/Program.cs` | **The OBO flow** — how `EnableTokenAcquisitionToCallDownstreamApi()` + `AddDownstreamApi()` wires up token exchange |
| `DownstreamApi/Program.cs` | How to protect a minimal API with Azure AD bearer tokens |
| `MiddleTierApi/appsettings.json` | How to configure the downstream API scopes for OBO |

---

## Common Issues

| Symptom | Fix |
|---------|-----|
| `AADSTS65001: The user or administrator has not consented` | Go to API permissions → Grant admin consent |
| `AADSTS700027: Client assertion contains an invalid signature` | Wrong client secret — regenerate in Azure Portal |
| `401 Unauthorized` on DownstreamApi | Check the `Audience` in appsettings matches the exposed API URI |
| `AADSTS50011: The redirect URI does not match` | Ensure ClientApp registration has `http://localhost` as redirect URI |
