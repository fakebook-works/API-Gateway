# Fakebook API Gateway

Fakebook API Gateway is the public GraphQL entry point for the Fakebook backend. It is a .NET 8 HotChocolate Fusion Gateway that composes backend subgraphs and forwards frontend requests to the correct service.

The composed subgraphs are `Authentication`, `SocialGraph`, `Recommendation`, `Search`, `Messaging`, `Notification`, and `Payment`. `Media` remains a direct upload service rather than a composed subgraph.

## Features

- Single public GraphQL endpoint at `/graphql`.
- HotChocolate Fusion composition through `fakebookGateway/gateway.far`.
- JWT access-token validation at the Gateway.
- Session validation against the Authentication subgraph.
- Canonical user registration through SocialGraph, followed by internal Authentication identity creation with the same user ID.
- Home stories, visited-group shortcuts, post creation, and authorization-aware post detail from SocialGraph.
- A hydrated recommendation query: Recommendation ranks IDs and Fusion batch-resolves each `post` through SocialGraph.
- Payment Premium queries/mutations and a hardened PayOS webhook proxy.
- Search queries, Messaging queries/mutations/subscriptions, and Notification feed/read/subscription operations.
- Trusted internal header forwarding to subgraphs.
- A dedicated named HTTP client and independently configurable Gateway secret for every subgraph.
- GraphQL-over-SSE forwarding without cookie-response buffering.
- HttpOnly refresh-cookie handling for login, refresh, logout, and logout-all flows.
- Public response scrubbing for raw refresh-token values.
- CORS configuration for local frontend development.

## Requirements

- .NET SDK 8.x for local development.
- Docker, if running the container image.
- Local subgraphs on the canonical ports: Authentication `1001`, SocialGraph `1002`,
  Recommendation `1003`, Search `1004`, Notification `1005`, Messaging `1006`, and
  Payment `1007`.
- JWT settings must match Authentication. `Gateway:InternalSharedSecret` is the fallback shared secret; any `Gateway:SubgraphSecrets:<Name>` override must match that target service.

## Configuration

Use `fakebookGateway/appsettings.example.json` as the safe reference config. Do not commit real secrets.

Important environment variables:

```text
ASPNETCORE_URLS=http://localhost:5099
Jwt__Issuer=fakebook-auth
Jwt__Audience=fakebook
Jwt__SigningKey=<same signing key as Authentication>
Gateway__InternalSharedSecret=<same shared secret as Authentication>
Gateway__SubgraphSecrets__Authentication=<optional Auth-specific secret>
Gateway__SubgraphSecrets__SocialGraph=<optional SocialGraph-specific secret>
Gateway__SubgraphSecrets__Recommendation=<optional Recommendation-specific secret>
Gateway__SubgraphSecrets__Search=<optional Search-specific secret>
Gateway__SubgraphSecrets__Messaging=<optional Messaging-specific secret>
Gateway__SubgraphSecrets__Notification=<optional Notification-specific secret>
Gateway__SubgraphSecrets__Payment=<optional Payment-specific secret>
Gateway__SessionCacheSeconds=30
Gateway__RefreshTokenCookieName=fb_refresh
Subgraphs__Authentication__Url=http://localhost:1001/graphql
Subgraphs__Payment__WebhookUrl=http://localhost:1007/internal/webhooks/payos
PaymentGateway__TimeoutSeconds=10
PaymentGateway__WebhookPermitLimit=60
PaymentGateway__WebhookWindowSeconds=60
```

## Run Locally

Start all composed subgraphs first, then run the Gateway. The committed `gateway.far`
uses production DNS. Development composition writes `gateway.local.far` with canonical
localhost ports and automatically uses the repo-local `.tools/nitro.exe` when Nitro is
not installed globally.

```powershell
dotnet restore .\fakebookGateway.sln
dotnet build .\fakebookGateway\fakebookGateway.csproj
.\fakebookGateway\compose-fusion.ps1 -Environment Development

$env:ASPNETCORE_URLS="http://localhost:5099"
$env:Jwt__Issuer="fakebook-auth"
$env:Jwt__Audience="fakebook"
$env:Jwt__SigningKey="<same signing key as Authentication>"
$env:Gateway__InternalSharedSecret="<same shared secret as Authentication>"
$env:Gateway__FusionArchivePath="gateway.local.far"
$env:Subgraphs__Authentication__Url="http://localhost:1001/graphql"
$env:Subgraphs__Payment__WebhookUrl="http://localhost:1007/internal/webhooks/payos"

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
2. SocialGraph calls Authentication `POST /internal/users` with only that exact ID, email, and password. This step is required.
3. If Authentication fails, SocialGraph removes the new profile and returns a failed payload.
4. After Authentication succeeds, SocialGraph concurrently calls Search `PUT /internal/search/indexes/{userId}` and Recommendation `PUT /internal/recommendation/users/{userId}/embedding`.
5. Search and Recommendation provisioning are idempotent and best-effort; SocialGraph returns the canonical ID even if a derived projection is temporarily unavailable.

Gateway does not call Search or Recommendation directly during registration. It exposes and routes the single SocialGraph mutation.

Authentication is email-only, has no phone identifier, and persists no SocialGraph profile fields. Frontend login/verification/password-reset `identifier` values currently contain email addresses. Name, username, birthdate, gender, location, and other profile reads must come from SocialGraph; Gateway trusted headers carry user/session identity, not profile data.

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
  -e Subgraphs__Payment__WebhookUrl="http://host.docker.internal:1007/internal/webhooks/payos" `
  ghcr.io/fakebook-works/api-gateway:main
```

Important: Fusion subgraph transport URLs are stored in `gateway.far`. The committed production archive uses the internal DNS names and ports `authentication:1001`, `social-graph:1002`, `recommendation:1003`, `search:1004`, `notification:1005`, `messaging:1006`, and `payment:1007`. Payment's REST webhook target is configured separately through `Subgraphs:Payment:WebhookUrl`.

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
.\compose-fusion.ps1 -Environment Production
.\compose-fusion.ps1 -Environment Development # writes gateway.local.far
```

Commit the updated schema files and `gateway.far`.

## Tests

```powershell
dotnet test .\fakebookGateway.sln
```

The permanent tests boot the composed archive, introspect the frontend-visible contract, assert Auth phone/internal/profile fields are absent while SocialGraph profile inputs remain public, verify recommendation/SocialGraph hydration, validate trusted-header replacement, and cover Payment Fusion plus webhook body/header/limit/status behavior.

## Documentation

Detailed agent/developer guides are available in:

- `fakebookGateway/AGENT.md`
- `fakebookGateway/AGENT_VIE.md`
- `fakebookGateway/Docs/socialgraph-feed-api.md`
