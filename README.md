# Fakebook API Gateway

Fakebook API Gateway is the public GraphQL entry point for the Fakebook backend. It is a .NET 8 HotChocolate Fusion Gateway that composes backend subgraphs and forwards frontend requests to the correct service.

The currently composed subgraphs are `Authentication`, `SocialGraph`, `Recommendation`, and `Payment`. Planned subgraphs include `Search`, `Messaging`, `Notification`, and `Media`.

## Features

- Single public GraphQL endpoint at `/graphql`.
- HotChocolate Fusion composition through `fakebookGateway/gateway.far`.
- JWT access-token validation at the Gateway.
- Session validation against the Authentication subgraph.
- Canonical user registration through SocialGraph, followed by internal Authentication identity creation with the same user ID.
- Home stories, visited-group shortcuts, post creation, and authorization-aware post detail from SocialGraph.
- A hydrated recommendation query: Recommendation ranks IDs and Fusion batch-resolves each `post` through SocialGraph.
- Payment Premium queries/mutations and a hardened PayOS webhook proxy.
- Trusted internal header forwarding to subgraphs.
- HttpOnly refresh-cookie handling for login, refresh, logout, and logout-all flows.
- Public response scrubbing for raw refresh-token values.
- CORS configuration for local frontend development.

## Requirements

- .NET SDK 8.x for local development.
- Docker, if running the container image.
- A running Authentication subgraph, usually at `http://localhost:5001/graphql`.
- A running SocialGraph subgraph, usually at `http://localhost:5223/graphql`.
- A running Recommendation subgraph, usually at `http://localhost:8000/graphql`.
- A running Payment subgraph, usually at `http://localhost:5016/graphql`.
- JWT settings must match Authentication. `Gateway:InternalSharedSecret` must match every service that validates trusted Gateway/internal calls.

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
Subgraphs__Payment__WebhookUrl=http://localhost:5016/internal/webhooks/payos
PaymentGateway__TimeoutSeconds=10
PaymentGateway__WebhookPermitLimit=60
PaymentGateway__WebhookWindowSeconds=60
```

## Run Locally

Start Authentication, SocialGraph, Recommendation, and Payment first, then run the Gateway:

```powershell
dotnet restore .\fakebookGateway.sln
dotnet build .\fakebookGateway\fakebookGateway.csproj

$env:ASPNETCORE_URLS="http://localhost:5099"
$env:Jwt__Issuer="fakebook-auth"
$env:Jwt__Audience="fakebook"
$env:Jwt__SigningKey="<same signing key as Authentication>"
$env:Gateway__InternalSharedSecret="<same shared secret as Authentication>"
$env:Subgraphs__Authentication__Url="http://localhost:5001/graphql"
$env:Subgraphs__Payment__WebhookUrl="http://localhost:5016/internal/webhooks/payos"

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

Registration is orchestrated behind that single mutation:

1. SocialGraph creates the profile and canonical Snowflake `userId`.
2. SocialGraph calls Authentication `POST /internal/users` with that exact ID, email/password credential, display name, date of birth, and gender. This step is required.
3. If Authentication fails, SocialGraph removes the new profile and returns a failed payload.
4. After Authentication succeeds, SocialGraph concurrently calls Search `PUT /internal/search/indexes/{userId}` and Recommendation `PUT /internal/recommendation/users/{userId}/embedding`.
5. Search and Recommendation provisioning are idempotent and best-effort; SocialGraph returns the canonical ID even if a derived projection is temporarily unavailable.

Gateway does not call Search or Recommendation directly during registration. It exposes and routes the single SocialGraph mutation.

Authentication is email-only and does not store or validate SocialGraph usernames. Profile/username reads must come from SocialGraph; Gateway trusted headers carry user ID and session ID, not username.

## SocialGraph Feed API

The following completed SocialGraph operations are public through Gateway:

```text
Query:    visitedGroups, postDetail, postDetails, homeStories, myStories
Mutation: createUser, recordGroupVisit, createFeedPost,
          createNormalStory, createShareStory, deleteStory
```

Raw graph operations and domain mutations without complete ownership authorization remain composition-internal.

The recommended feed is a single composed query. Recommendation owns ordering; SocialGraph owns post data, privacy, block checks, and user/group post discrimination:

```graphql
query RecommendedFeed($userId: ID!, $skip: Int! = 0, $take: Int! = 20) {
  recommendFeed(userId: $userId, skip: $skip, take: $take) {
    postId
    post {
      __typename
      ... on FeedPostDetail {
        id content privacy create
        author { id name avatar isVerified canFollow }
        media { id type url }
      }
      ... on GroupPostDetail {
        id content privacy create
        author { id name avatar isVerified canFollow }
        group { id name avatar canJoin }
        media { id type url }
      }
    }
  }
}
```

`post` is nullable because a ranked candidate may be deleted, blocked, or made private between candidate generation and hydration. Frontend should omit null items. See `fakebookGateway/Docs/socialgraph-feed-api.md` for complete operations, variables, paging, errors, and frontend handling.

## Payment Premium

The composed public Payment surface is:

```text
Query:    premiumPlans, premiumOrder
Mutation: createPremiumCheckout
```

Authentication's `paymentPremiumState` and `setPaymentValidDate` fields are internal composition fields. Frontend must use Payment operations and read `UserType.validDate` for the current Auth premium expiry when needed.

PayOS calls `POST /api/webhooks/payos` on Gateway. The route accepts JSON only, limits the raw request body to 64 KiB, rate limits by client IP, and forwards only the exact body bytes, correlation ID, and server-owned Gateway secret to Payment's protected webhook endpoint. Browser authorization, cookies, and spoofed trusted headers are never forwarded.

## Run With Docker

A prebuilt image is available:

```powershell
docker run --rm -p 5099:8080 `
  -e Jwt__Issuer="fakebook-auth" `
  -e Jwt__Audience="fakebook" `
  -e Jwt__SigningKey="<same signing key as Authentication>" `
  -e Gateway__InternalSharedSecret="<same shared secret as Authentication>" `
  -e Subgraphs__Authentication__Url="http://host.docker.internal:5001/graphql" `
  -e Subgraphs__Payment__WebhookUrl="http://host.docker.internal:5016/internal/webhooks/payos" `
  ghcr.io/fakebook-works/api-gateway:main
```

Important: Fusion subgraph transport URLs are stored in `gateway.far`. The current development archive points Authentication to `http://localhost:5001/graphql`, SocialGraph to `http://localhost:5223/graphql`, Recommendation to `http://localhost:8000/graphql`, and Payment to `http://localhost:5016/graphql`. For Docker deployments, recompose `gateway.far` with transport URLs reachable inside the container network. Payment's REST webhook target is configured separately through `Subgraphs:Payment:WebhookUrl`.

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
  --source-schema-file .\Gateway\schema\Recommendation `
  --source-schema-file .\Gateway\schema\Payment `
  --archive .\gateway.far `
  --env Development
```

Commit the updated schema files and `gateway.far`.

## Tests

```powershell
dotnet test .\fakebookGateway.sln
```

The 17 permanent tests boot the composed archive, introspect the frontend-visible contract, assert Auth internal fields and identity username are absent, verify recommendation/SocialGraph hydration, validate trusted-header replacement, and cover Payment Fusion plus webhook body/header/limit/status behavior.

## Documentation

Detailed agent/developer guides are available in:

- `fakebookGateway/AGENT.md`
- `fakebookGateway/AGENT_VIE.md`
- `fakebookGateway/Docs/socialgraph-feed-api.md`
