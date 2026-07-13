# Fakebook API Gateway

Fakebook API Gateway is the public GraphQL entry point for the Fakebook backend. It is a .NET 8 HotChocolate Fusion Gateway that composes backend subgraphs and forwards frontend requests to the correct service.

The current composed subgraphs are `Authentication` and `Payment`. Planned subgraphs include `Search`, `SocialGraph`, `Recommendation`, `Messaging`, `Notification`, and `Media`.

## Features

- Single public GraphQL endpoint at `/graphql`.
- HotChocolate Fusion composition through `fakebookGateway/gateway.far`.
- JWT access-token validation at the Gateway.
- Session validation against the Authentication subgraph.
- Trusted internal header forwarding to subgraphs.
- HttpOnly refresh-cookie handling for login, refresh, logout, and logout-all flows.
- Public response scrubbing for raw refresh-token values.
- CORS configuration for local frontend development.
- Authentication registration exposes required boolean `gender` (`true` = Male, `false` = Female), while `UserType.gender` remains nullable for pre-migration users.
- Payment Premium GraphQL composition and hardened PayOS webhook proxying.

## Requirements

- .NET SDK 8.x for local development.
- Docker, if running the container image.
- A running Authentication subgraph, usually at `http://localhost:5001/graphql`.
- JWT settings and `Gateway:InternalSharedSecret` must match the Authentication service.

## Configuration

Use `fakebookGateway/appsettings.example.json` as the safe reference config. Do not commit real secrets.

Important environment variables:

```text
ASPNETCORE_URLS=http://localhost:5099
Jwt__Issuer=fakebook-auth
Jwt__Audience=fakebook
Jwt__SigningKey=<same signing key as Authentication>
Gateway__InternalSharedSecret=<same shared secret as Authentication>
Gateway__SessionCacheSeconds=30
Gateway__RefreshTokenCookieName=fb_refresh
Subgraphs__Authentication__Url=http://localhost:5001/graphql
```

## Run Locally

Start the Authentication subgraph first, then run the Gateway:

```powershell
dotnet restore .\fakebookGateway.sln
dotnet build .\fakebookGateway\fakebookGateway.csproj

$env:ASPNETCORE_URLS="http://localhost:5099"
$env:Jwt__Issuer="fakebook-auth"
$env:Jwt__Audience="fakebook"
$env:Jwt__SigningKey="<same signing key as Authentication>"
$env:Gateway__InternalSharedSecret="<same shared secret as Authentication>"
$env:Subgraphs__Authentication__Url="http://localhost:5001/graphql"

dotnet run --project .\fakebookGateway\fakebookGateway.csproj
```

Gateway endpoint:

```text
http://localhost:5099/graphql
```

## Run With Docker

A prebuilt image is available:

```powershell
docker run --rm -p 5099:8080 `
  -e Jwt__Issuer="fakebook-auth" `
  -e Jwt__Audience="fakebook" `
  -e Jwt__SigningKey="<same signing key as Authentication>" `
  -e Gateway__InternalSharedSecret="<same shared secret as Authentication>" `
  -e Subgraphs__Authentication__Url="http://host.docker.internal:5001/graphql" `
  ghcr.io/fakebook-works/api-gateway:main
```

Important: Fusion subgraph transport URLs are stored in `gateway.far`. The current development archive points Authentication to `http://localhost:5001/graphql`. For Docker deployments, make sure the archive was composed with an Auth URL reachable from inside the container, such as the production service name `http://authentication/graphql`, or recompose `gateway.far` for your Docker network before building the image.

Build locally instead:

```powershell
docker build -t fakebook-api-gateway -f .\fakebookGateway\Dockerfile .
docker run --rm -p 5099:8080 fakebook-api-gateway
```

## Fusion Schema

Gateway composition files live under:

```text
fakebookGateway/Gateway/schema/<SubgraphName>/
```

Each subgraph should provide:

- `schema.graphqls`
- `schema-settings.json`
- optional `schema-extensions.graphqls`

Recompose the Fusion archive after schema changes:

```powershell
cd .\fakebookGateway
nitro fusion compose `
  --source-schema-file .\Gateway\schema\Authentication `
  --source-schema-file .\Gateway\schema\Payment `
  --archive .\gateway.far `
  --env Development
```

Commit the updated schema files and `gateway.far`.

The current Authentication contract includes:

```graphql
input RegisterInput {
  displayName: String!
  dob: Date
  email: String!
  gender: Boolean!
  username: String!
  password: String!
}

type UserType {
  gender: Boolean
  validDate: DateTime
}
```

The composed public Payment surface is:

```text
premiumPlans
premiumOrder
createPremiumCheckout
```

Authentication's `validateGatewaySession`, `paymentPremiumState`, and `setPaymentValidDate` fields are marked `@internal` and are absent from the public schema.

PayOS posts to the public Gateway route `POST /api/webhooks/payos`. Gateway accepts JSON only, limits the raw body to 64 KiB, rate limits by client IP, strips caller credentials/trusted headers, and forwards only the raw body, correlation id, and the real Gateway secret to Payment's `/internal/webhooks/payos`. Downstream bodies and headers are never exposed.

## Documentation

Detailed agent/developer guides are available in:

- `fakebookGateway/AGENT.md`
- `fakebookGateway/AGENT_VIE.md`
