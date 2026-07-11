# Fakebook API Gateway

Fakebook API Gateway is the public GraphQL entry point for the Fakebook backend. It is a .NET 8 HotChocolate Fusion Gateway that composes backend subgraphs and forwards frontend requests to the correct service.

The currently composed subgraphs are `Authentication` and `SocialGraph`. Only the canonical `createUser` registration mutation is exposed from SocialGraph for now. Planned subgraphs include `Search`, `Recommendation`, `Messaging`, `Notification`, and `Media`.

## Features

- Single public GraphQL endpoint at `/graphql`.
- HotChocolate Fusion composition through `fakebookGateway/gateway.far`.
- JWT access-token validation at the Gateway.
- Session validation against the Authentication subgraph.
- Canonical user registration through SocialGraph, followed by internal Authentication identity creation with the same user ID.
- Trusted internal header forwarding to subgraphs.
- HttpOnly refresh-cookie handling for login, refresh, logout, and logout-all flows.
- Public response scrubbing for raw refresh-token values.
- CORS configuration for local frontend development.

## Requirements

- .NET SDK 8.x for local development.
- Docker, if running the container image.
- A running Authentication subgraph, usually at `http://localhost:5001/graphql`.
- A running SocialGraph subgraph, usually at `http://localhost:5223/graphql`.
- JWT settings must match Authentication. `Gateway:InternalSharedSecret` must be the same in Gateway, SocialGraph, and Authentication so SocialGraph can use Auth's protected internal API.

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

Start the Authentication and SocialGraph subgraphs first, then run the Gateway:

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

## User Registration

The public registration entry point is SocialGraph's `createUser`. The legacy Authentication `register` mutation is internal in the Gateway composition to prevent Auth-only users.

```graphql
mutation CreateUser($input: CreateUserInput!) {
  createUser(input: $input) {
    success
    userId
    message
  }
}
```

```json
{
  "input": {
    "name": "Nguyen Van A",
    "gender": true,
    "birthdate": "2000-01-01",
    "location": "Ha Noi",
    "email": "a@example.com",
    "password": "secret123"
  }
}
```

On success, SocialGraph owns the generated canonical user ID and Authentication stores its identity record with that exact ID.

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

Important: Fusion subgraph transport URLs are stored in `gateway.far`. The current development archive points Authentication to `http://localhost:5001/graphql` and SocialGraph to `http://localhost:5223/graphql`. For Docker deployments, recompose `gateway.far` with transport URLs reachable inside the container network.

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
  --source-schema-file .\Gateway\schema\SocialGraph `
  --archive .\gateway.far `
  --env Development
```

Commit the updated schema files and `gateway.far`.

## Documentation

Detailed agent/developer guides are available in:

- `fakebookGateway/AGENT.md`
- `fakebookGateway/AGENT_VIE.md`
