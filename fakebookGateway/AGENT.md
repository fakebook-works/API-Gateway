# Fakebook API Gateway Agent Guide

This file is the working guide for developers and AI agents modifying the Fakebook API Gateway or integrating new Fakebook subgraphs.

The Gateway is the public GraphQL entry point for the frontend. It composes multiple smaller GraphQL subgraphs through HotChocolate Fusion and forwards requests to the responsible service.

## Project Summary

- Project type: .NET 8 ASP.NET Core GraphQL Gateway.
- GraphQL stack: HotChocolate + HotChocolate Fusion.
- Main public endpoint: `/graphql`.
- Root route: `/` redirects to `/graphql`.
- Composition artifact: `fakebookGateway/gateway.far`.
- Current composed subgraphs: `Authentication`, `Payment`.
- Planned subgraphs: `Search`, `SocialGraph`, `Recommendation`, `Messaging`, `Notification`, `Media`.
- Auth model: Gateway validates JWT locally and validates active session status with the Authentication subgraph.
- Refresh token model: Gateway owns browser cookies. Auth returns cookie instructions; Gateway applies them and scrubs raw refresh token values from public GraphQL responses.

## What The Gateway Can Do

Current capabilities:

- Expose a single public GraphQL endpoint for frontend clients.
- Load a Fusion archive from disk with `.AddFileSystemConfiguration(...)`.
- Proxy GraphQL operations to the Authentication subgraph.
- Validate HS256 JWT access tokens using configured issuer, audience, and signing key.
- Validate access-token session state against Auth through the internal `validateGatewaySession` query.
- Cache Auth session validation results for a short configurable TTL.
- Strip browser-supplied trusted internal headers before any downstream call.
- Forward trusted identity context to subgraphs:
  - `X-User-Id`
  - `X-Session-Id`
  - `X-Username`
  - `X-Correlation-ID`
  - `Authorization`
  - `X-Refresh-Token`
  - `X-Gateway-Secret`
- Read the refresh token from the configured HttpOnly cookie and forward it to Auth as `X-Refresh-Token`.
- Consume Auth cookie instructions from the internal response header `X-Fakebook-Refresh-Cookie-Instruction`.
- Consume cookie instructions selected in GraphQL response payloads.
- Set and clear browser refresh cookies.
- Null out raw `refreshToken` scalar fields in public GraphQL responses.
- Null out `GatewayCookieInstruction.value` in public GraphQL responses.
- Add `X-Correlation-ID` to responses.
- Apply CORS for configured frontend origins with credentials enabled.
- Build and publish a Docker image through the existing GitHub Actions workflow.

Current limitations:

- Authentication and Payment are currently composed in `gateway.far`.
- Permanent Payment webhook/Fusion tests live in `fakebookGateway.Tests`.
- Fusion composition is currently a manual local workflow.
- The Gateway does not currently implement field-level authorization rules. Subgraphs must protect their own private operations using the internal headers and `X-Gateway-Secret`, or the Gateway must be extended with field policy before exposing sensitive fields from a weak subgraph.
- Fusion URLs are baked into `gateway.far` during composition. Runtime `Subgraphs:*:Url` config is currently used by Gateway-owned internal clients such as Auth session validation, not as generic service discovery for every Fusion transport.

## Important Files

- `fakebookGateway/Program.cs`: service registration, JWT auth, CORS, middleware order, Fusion archive loading, GraphQL endpoint mapping.
- `fakebookGateway/Gateway/GatewayOptions.cs`: Gateway and JWT runtime options.
- `fakebookGateway/Gateway/GatewayConstants.cs`: trusted internal headers, claim names, and request item keys.
- `fakebookGateway/Gateway/GatewayEdgeMiddleware.cs`: public-edge cleanup of trusted headers, correlation id handling, and session validation middleware.
- `fakebookGateway/Gateway/AuthSessionValidator.cs`: internal Auth call for `validateGatewaySession`, with memory cache.
- `fakebookGateway/Gateway/FusionSubgraphHeaderHandler.cs`: outgoing subgraph HTTP handler that injects trusted headers and consumes internal cookie instruction headers.
- `fakebookGateway/Gateway/GatewayCookieInstructionProcessor.cs`: applies `SET` and `CLEAR` cookie instructions to the browser response.
- `fakebookGateway/Gateway/GraphQlCookieResponseMiddleware.cs`: rewrites GraphQL responses to scrub refresh tokens and cookie instruction values.
- `fakebookGateway/Gateway/schema/<SubgraphName>/schema.graphqls`: exported source schema for each composed subgraph.
- `fakebookGateway/Gateway/schema/<SubgraphName>/schema-settings.json`: Fusion transport settings for each source schema.
- `fakebookGateway/Gateway/schema/<SubgraphName>/schema-extensions.graphqls`: Gateway-owned source-schema extensions such as `@internal`.
- `fakebookGateway/gateway.far`: composed Fusion archive loaded at runtime.
- `fakebookGateway/appsettings.example.json`: safe example runtime configuration.
- `fakebookGateway/Dockerfile`: container build.
- `.github/workflows/docker-build.yml`: GHCR Docker build and push.

Do not commit real secrets. Keep real JWT signing keys, gateway shared secrets, database passwords, SMTP passwords, and cloud credentials out of tracked config.

## Runtime Configuration

Important configuration sections:

```text
Jwt
Gateway
Subgraphs
```

Required JWT configuration:

```text
Jwt__Issuer
Jwt__Audience
Jwt__SigningKey
```

`Jwt:SigningKey` must be at least 32 bytes and must match the Authentication subgraph signing key because Auth issues the access tokens and the Gateway validates them.

Required Gateway configuration:

```text
Gateway__InternalSharedSecret
```

The internal shared secret must match the Authentication subgraph `Gateway:InternalSharedSecret`. Use at least 32 bytes even though the Gateway currently validates only non-empty value.

Useful environment variables:

```text
ASPNETCORE_URLS
Jwt__Issuer
Jwt__Audience
Jwt__SigningKey
Gateway__FusionArchivePath
Gateway__InternalSharedSecret
Gateway__SessionCacheSeconds
Gateway__RefreshTokenCookieName
Gateway__AllowedOrigins__0
Gateway__AllowedOrigins__1
Gateway__AllowedOrigins__2
Subgraphs__Authentication__Url
Subgraphs__Authentication__GraphQLEndpoint
```

Default Gateway values:

```text
FusionArchivePath = gateway.far
AuthenticationGraphQLEndpoint = http://localhost:5001/graphql
SessionCacheSeconds = 30
RefreshTokenCookieName = fb_refresh
AllowedOrigins = http://localhost:3000, http://localhost:5173, http://localhost:5174
```

`Subgraphs__Authentication__Url` is used by the Gateway internal Auth session validator. The Fusion transport URL for Authentication is configured in `Gateway/schema/Authentication/schema-settings.json` and composed into `gateway.far`.

## Middleware Order

Current order in `Program.cs`:

```text
GatewayEdgeMiddleware
CORS
Authentication
Authorization
GatewaySessionValidationMiddleware
GraphQlCookieResponseMiddleware
MapGraphQL("/graphql")
```

Why this order matters:

- `GatewayEdgeMiddleware` must run early so browser-supplied trusted headers are stripped before any auth or proxy logic.
- CORS runs before GraphQL so frontend credentialed requests can work.
- JWT authentication must run before session validation.
- Session validation must run before GraphQL execution so invalid sessions are rejected at the Gateway.
- GraphQL cookie response middleware wraps GraphQL execution so it can apply cookie instructions and scrub sensitive fields.

## Public Request Flow

Unauthenticated public operation:

```text
Frontend -> Gateway /graphql
Gateway strips trusted headers
Gateway assigns/reuses X-Correlation-ID
No bearer token is present
Gateway forwards operation through Fusion
Subgraph decides whether the operation is public
Gateway scrubs token/cookie values from GraphQL response if present
Gateway returns response to frontend
```

Authenticated operation:

```text
Frontend -> Gateway /graphql with Authorization: Bearer <accessToken>
Gateway strips trusted headers
Gateway validates JWT signature, issuer, audience, nbf, exp
Gateway extracts user_id, sid, username
Gateway calls Auth validateGatewaySession with X-Gateway-Secret
Gateway caches positive/negative session validation briefly
Gateway stores trusted identity context in HttpContext.Items
Fusion forwards request to subgraph with internal headers
Subgraph resolves operation
Gateway applies cookie instructions if any
Gateway scrubs raw refresh token values
Gateway returns response
```

Revoked session behavior:

- Auth access tokens include `sid`.
- Gateway calls Auth to ensure `sid` is still active.
- If Auth returns invalid, Gateway responds with HTTP 401 and GraphQL error code `UNAUTHENTICATED`.
- Positive validation can be cached for up to `Gateway:SessionCacheSeconds`, capped by session expiry.
- If you need near-immediate revocation in local testing, reduce `Gateway__SessionCacheSeconds`.

## Internal Header Contract

Gateway-generated headers:

```text
Authorization: Bearer <accessToken>
X-User-Id: <current-user-id>
X-Session-Id: <current-session-id>
X-Username: <current-username>
X-Correlation-ID: <request-correlation-id>
X-Refresh-Token: <raw-refresh-token-from-HttpOnly-cookie>
X-Gateway-Secret: <Gateway__InternalSharedSecret>
```

Rules:

- Browsers must not be allowed to set trusted identity headers.
- Gateway strips `X-User-Id`, `X-Session-Id`, `X-Username`, `X-Gateway-Secret`, and `X-Refresh-Token` from public requests.
- Gateway regenerates trusted headers before subgraph calls.
- Subgraphs should trust these headers only when `X-Gateway-Secret` is valid and the request arrives through a trusted internal network path.
- Subgraphs should use `X-Correlation-ID` in logs and outgoing calls.
- Non-Auth subgraphs should not read browser cookies and should not handle refresh tokens.

## Refresh Token And Cookie Flow

Auth owns refresh token generation and rotation. Gateway owns the browser cookie.

Login:

```text
Frontend calls login through Gateway
Auth returns accessToken, refreshToken, refreshTokenCookie
Auth may also return X-Fakebook-Refresh-Cookie-Instruction header
Gateway sets HttpOnly refresh cookie
Gateway nulls raw refreshToken in public response
Gateway nulls refreshTokenCookie.value in public response
Frontend receives access token and user data
```

Refresh:

```text
Frontend calls refreshToken through Gateway, usually with no input
Gateway reads fb_refresh or configured cookie name
Gateway forwards raw refresh token to Auth through X-Refresh-Token
Auth rotates refresh token
Gateway sets replacement cookie
Gateway returns new access token
Gateway scrubs raw refresh token values
```

Logout:

```text
Frontend calls logout through Gateway, usually with no input
Gateway reads refresh cookie and forwards X-Refresh-Token
Auth revokes session
Gateway clears refresh cookie using returned instruction
```

Logout all:

```text
Frontend calls logoutAll with bearer access token
Gateway validates JWT/session
Auth revokes all sessions for current user
Gateway clears current browser refresh cookie
```

Logout session:

```text
Frontend calls logoutSession(sessionId)
Gateway validates JWT/session
Auth revokes target session
If target is current session, Gateway clears current cookie
If target is another session, Gateway leaves current cookie unchanged
```

Implementation notes:

- `FusionSubgraphHeaderHandler` consumes `X-Fakebook-Refresh-Cookie-Instruction` from subgraph HTTP responses and removes that internal header.
- `GraphQlCookieResponseMiddleware` also scans GraphQL response JSON for cookie instruction objects.
- Any scalar property named `refreshToken` is nulled in public responses. Do not introduce unrelated public fields named `refreshToken` in future subgraphs.
- Any object shaped like a cookie instruction (`operation`, `name`, `path`, `maxAgeSeconds`) may trigger cookie processing. Do not reuse that shape for unrelated data.

## Current Public GraphQL Surface

The currently composed public surface comes from Authentication and Payment.

Queries:

```text
health
me
mySessions
mySessionHistory
premiumPlans
premiumOrder
```

Mutations:

```text
register
verifyEmail
login
refreshToken
logout
logoutAll
logoutSession
resendEmailVerification
requestPasswordReset
resetPassword
changePassword
createPremiumCheckout
```

Authentication gender contract:

```graphql
input RegisterInput {
  displayName: String!
  dob: Date
  email: String!
  gender: Boolean!
  username: String!
  password: String!
}

extend type UserType {
  gender: Boolean
  validDate: DateTime
}
```

`true` means Male and `false` means Female. The output is nullable so identities created before the database migration remain readable.

Internal Auth fields:

```text
validateGatewaySession
paymentPremiumState
setPaymentValidDate
```

These fields exist in the Authentication source schema but are marked `@internal` in Gateway schema extensions, so they must not be visible in the public Gateway schema.

## Fusion Schema Layout

Each subgraph has a folder:

```text
fakebookGateway/Gateway/schema/<SubgraphName>/
  schema.graphqls
  schema-settings.json
  schema-extensions.graphqls   optional
```

`schema.graphqls`:

- Exported source schema from the subgraph service.
- Should be committed.
- Must match the running subgraph version.

`schema-settings.json`:

```json
{
  "name": "SubgraphName",
  "transports": {
    "http": {
      "url": "{{SUBGRAPH_NAME_URL}}",
      "clientName": "fusion"
    }
  },
  "environments": {
    "Development": {
      "SUBGRAPH_NAME_URL": "http://localhost:5010/graphql"
    },
    "Production": {
      "SUBGRAPH_NAME_URL": "http://subgraph-service/graphql"
    }
  }
}
```

Recommended default:

- Use `clientName: "fusion"` for normal subgraphs so `FusionSubgraphHeaderHandler` is applied.
- Use a custom client name only when a subgraph needs special HTTP behavior. If you do, register the client in `Program.cs` and add `FusionSubgraphHeaderHandler`.

`schema-extensions.graphqls`:

- Gateway-owned source schema extensions.
- Use it to mark fields `@internal`.
- Use it for composition-only metadata.
- Do not put business schema here unless it is truly Gateway-owned composition metadata.

Example:

```graphql
extend type Query {
  validateGatewaySession(input: GatewaySessionValidationInput!): GatewaySessionValidationPayload! @internal
}
```

## Composing The Fusion Archive

Required local tool:

```powershell
dotnet tool install -g ChilliCream.Nitro.CommandLine --version 16.1.3
```

Compose from the `fakebookGateway` directory:

```powershell
cd .\fakebookGateway
nitro fusion compose `
  --source-schema-file .\Gateway\schema\Authentication `
  --archive .\gateway.far `
  --env Development
```

When more subgraphs exist, include every source schema folder:

```powershell
cd .\fakebookGateway
nitro fusion compose `
  --source-schema-file .\Gateway\schema\Authentication `
  --source-schema-file .\Gateway\schema\Search `
  --source-schema-file .\Gateway\schema\SocialGraph `
  --source-schema-file .\Gateway\schema\Recommendation `
  --source-schema-file .\Gateway\schema\Messaging `
  --source-schema-file .\Gateway\schema\Notification `
  --source-schema-file .\Gateway\schema\Media `
  --archive .\gateway.far `
  --env Development
```

For production composition:

```powershell
nitro fusion compose `
  --source-schema-file .\Gateway\schema\Authentication `
  --archive .\gateway.far `
  --env Production
```

After composition:

- Commit updated `schema.graphqls`, `schema-settings.json`, `schema-extensions.graphqls`, and `gateway.far`.
- Build the Gateway.
- Run Auth and Gateway smoke tests.
- Verify the public schema does not expose fields marked `@internal`.

## Local Commands

Build:

```powershell
dotnet build .\fakebookGateway\fakebookGateway.csproj --no-restore
```

Run:

```powershell
dotnet run --project .\fakebookGateway\fakebookGateway.csproj
```

Example local run with Auth on port `5001` and Gateway on port `5099`:

```powershell
$env:ASPNETCORE_URLS="http://localhost:5099"
$env:Jwt__Issuer="fakebook-auth"
$env:Jwt__Audience="fakebook"
$env:Jwt__SigningKey="<same-signing-key-as-auth-at-least-32-bytes>"
$env:Gateway__InternalSharedSecret="<same-secret-as-auth-at-least-32-bytes>"
$env:Subgraphs__Authentication__Url="http://localhost:5001/graphql"
dotnet run --project .\fakebookGateway\fakebookGateway.csproj
```

GraphQL endpoint:

```text
http://localhost:5099/graphql
```

## How To Implement A New Subgraph

Use this checklist when adding `Search`, `SocialGraph`, `Recommendation`, `Messaging`, `Notification`, `Media`, or any future subgraph.

### 1. Define Ownership

Before writing code, define the bounded context:

- What data does the subgraph own?
- Which database/schema does it own?
- Which GraphQL fields belong to it?
- Which operations are public?
- Which operations require an authenticated user?
- Which operations require a specific ownership/permission check?
- Which events or calls does it need from other services?

Do not duplicate ownership:

- Authentication owns identity, credentials, sessions, OTP, and token issuing.
- SocialGraph should own relationships such as friend/follow/block edges.
- Media should own media metadata, upload state, and access decisions for media.
- Messaging should own conversations, participants, messages, and read state.
- Notification should own notification records and delivery state.
- Search should own searchable indexes and search query behavior.
- Recommendation should own ranking inputs, feature retrieval, and recommendations.

### 2. Create The Subgraph Service

Recommended baseline for .NET subgraphs:

- .NET 8 ASP.NET Core.
- HotChocolate GraphQL.
- `HotChocolate.AspNetCore.CommandLine` for schema export.
- Explicit schema name matching the subgraph name.
- `/graphql` endpoint.
- Correlation id middleware using `X-Correlation-ID`.
- Internal gateway secret validation.
- Structured errors with stable `extensions.code`.

Minimum GraphQL setup:

```csharp
builder.Services
    .AddGraphQLServer("Search")
    .AddQueryType<Query>()
    .AddMutationType<Mutation>();

app.MapGraphQL();
app.RunWithGraphQLCommands(args);
```

The schema name must be stable because export commands and Fusion composition can refer to it.

### 3. Add Gateway Header Validation In The Subgraph

Every subgraph should reject direct browser access for protected operations.

Recommended behavior:

- Require `X-Gateway-Secret` for internal-only subgraphs.
- For public operations, requiring `X-Gateway-Secret` is still acceptable because public clients should call through Gateway.
- For protected operations, require valid `X-Gateway-Secret`, `X-User-Id`, and usually `X-Session-Id`.
- Use `X-User-Id` as the authoritative current user id.
- Use `X-Username` only as display context, not as authority.
- Use `Authorization` only if the subgraph intentionally validates JWT itself.
- Never trust a browser-supplied user id or session id.

Suggested context object:

```csharp
public sealed record GatewayUserContext(
    long? UserId,
    long? SessionId,
    string? Username,
    string CorrelationId);
```

Suggested resolver pattern:

```csharp
public async Task<IReadOnlyList<ResultType>> MyProtectedData(
    [Service] IGatewayUserContextAccessor contextAccessor,
    CancellationToken cancellationToken)
{
    var user = contextAccessor.RequireUser();
    return await service.GetForUserAsync(user.UserId, cancellationToken);
}
```

Use GraphQL errors like:

```text
UNAUTHENTICATED
FORBIDDEN
INVALID_INPUT
NOT_FOUND
CONFLICT
RATE_LIMITED
```

### 4. Design A Gateway-Friendly GraphQL Schema

General rules:

- Keep the schema owned by the subgraph.
- Prefer stable, explicit fields over leaking database table shapes.
- Use `Long` for Fakebook Snowflake IDs unless a subgraph has a strong reason to expose `ID`.
- Use `DateTime` with offsets for timestamps.
- Use `Date` for calendar-only values.
- Do not expose raw secrets, internal tokens, provider credentials, or storage keys.
- Avoid public fields named `refreshToken` outside Authentication because Gateway scrubs scalar `refreshToken` values.
- Avoid object shapes that accidentally look like `GatewayCookieInstruction`.
- Keep mutation payloads consistent: include `success`, `message`, and the changed object where useful.
- Use cursor pagination for unbounded lists.
- Avoid large nested responses that force expensive cross-service joins.
- Use explicit error codes in GraphQL errors.

Recommended naming:

```text
Query.searchPosts
Query.myFriends
Query.myNotifications
Query.conversation
Mutation.sendMessage
Mutation.markNotificationRead
Mutation.createPostMediaUpload
```

Avoid generic names that will collide across subgraphs:

```text
Query.items
Query.list
Mutation.create
Mutation.update
```

### 5. Export The Subgraph Schema

For a HotChocolate subgraph with command-line support:

```powershell
dotnet run --project <path-to-subgraph.csproj> --no-build -- schema export --schema-name <SubgraphName> --output <gateway-repo>\fakebookGateway\Gateway\schema\<SubgraphName>\schema.graphqls
```

Example:

```powershell
dotnet run --project ..\Backend-Search\fakebookSearch\fakebookSearch.csproj --no-build -- schema export --schema-name Search --output .\fakebookGateway\Gateway\schema\Search\schema.graphqls
```

If the subgraph is not HotChocolate:

- Export or write a valid GraphQL SDL file.
- Save it as `fakebookGateway/Gateway/schema/<SubgraphName>/schema.graphqls`.
- Ensure custom scalars and directives are defined.

### 6. Add Fusion Settings

Create:

```text
fakebookGateway/Gateway/schema/<SubgraphName>/schema-settings.json
```

Template:

```json
{
  "name": "Search",
  "transports": {
    "http": {
      "url": "{{SEARCH_URL}}",
      "clientName": "fusion"
    }
  },
  "environments": {
    "Development": {
      "SEARCH_URL": "http://localhost:5010/graphql"
    },
    "Production": {
      "SEARCH_URL": "http://search/graphql"
    }
  }
}
```

Use a custom `clientName` only when needed:

```json
"clientName": "search-fusion"
```

Then register it in `Program.cs`:

```csharp
builder.Services
    .AddHttpClient("search-fusion")
    .AddHttpMessageHandler<FusionSubgraphHeaderHandler>();
```

### 7. Add Schema Extensions If Needed

Create:

```text
fakebookGateway/Gateway/schema/<SubgraphName>/schema-extensions.graphqls
```

Use it for Gateway composition metadata:

```graphql
extend type Query {
  internalDebugField: String! @internal
}
```

Common uses:

- Hide internal fields with `@internal`.
- Add Fusion metadata required for composition.
- Resolve naming or ownership concerns without changing generated source schema.

Do not use schema extensions to hide security mistakes. If a field is sensitive and should never be reachable, remove it from the subgraph schema or enforce authorization in the subgraph.

### 8. Compose Gateway Archive

Run `nitro fusion compose` from `fakebookGateway` and include every subgraph folder. Composition must include all source schemas, not only the newly added one.

```powershell
cd .\fakebookGateway
nitro fusion compose `
  --source-schema-file .\Gateway\schema\Authentication `
  --source-schema-file .\Gateway\schema\Search `
  --archive .\gateway.far `
  --env Development
```

### 9. Configure Local Ports

Pick stable local ports to avoid collisions. Suggested starting points:

```text
Authentication = 5001
Gateway = 5099
Search = 5010
SocialGraph = 5011
Recommendation = 5012
Messaging = 5013
Notification = 5014
Media = 5015
```

Update each subgraph `schema-settings.json` with matching Development URLs, then recompose `gateway.far`.

### 10. Test Through The Gateway

Minimum checks for every new subgraph:

- Subgraph starts and responds to direct `health`.
- Gateway starts with the recomposed `gateway.far`.
- Gateway public schema includes intended fields.
- Gateway public schema hides `@internal` fields.
- Public operations work without bearer token only if intentionally public.
- Protected operations fail without bearer token.
- Protected operations succeed with a valid Auth-issued access token.
- Protected operations fail after logout/revoked session.
- Browser-supplied `X-User-Id`/`X-Gateway-Secret` spoofing does not bypass auth.
- `X-Correlation-ID` reaches the subgraph logs.
- Subgraph receives `X-User-Id`, `X-Session-Id`, and `X-Username` for authenticated calls.

## Subgraph Development Guidelines

Security:

- Treat Gateway as the public edge, not as the only security layer.
- Require `X-Gateway-Secret` for protected subgraph operations.
- Require `X-User-Id` for user-scoped reads and mutations.
- Check resource ownership inside the owning subgraph.
- Do not trust IDs sent in GraphQL input as authority.
- Do not call Auth database directly from non-Auth subgraphs.
- Do not accept refresh tokens in non-Auth subgraphs.
- Do not log tokens, secrets, OTPs, passwords, cookies, or credential-bearing URLs.

Reliability:

- Propagate `X-Correlation-ID`.
- Use cancellation tokens in resolvers and data access.
- Keep mutations idempotent where product behavior allows.
- Use stable error codes.
- Put timeouts around outbound calls.
- Avoid cross-subgraph synchronous chains in hot paths.

Schema quality:

- Define clear ownership for every type and field.
- Keep inputs explicit and validated.
- Use pagination for lists.
- Keep payloads predictable.
- Prefer additive schema changes.
- Avoid renaming fields after frontend integration unless coordinated.
- Avoid duplicate root fields across subgraphs.

Data consistency:

- Each subgraph owns its database writes.
- Cross-service data should be referenced by IDs.
- For denormalized read models, define source of truth and refresh strategy.
- Use events/outbox later if workflows need asynchronous cross-service consistency.

Testing:

- Add direct subgraph tests for business rules.
- Add Gateway proxy tests for composed behavior.
- Test unauthorized, unauthenticated, and revoked-session paths.
- Test schema composition after every schema change.

## Planned Subgraph Notes

Authentication:

- Already implemented.
- Owns identity, credentials, sessions, OTP, JWT issuing, refresh token rotation, and cookie instruction contract.
- Exposes `validateGatewaySession` for Gateway internal use.

Search:

- Should own search indexes and query behavior.
- Should not own source content.
- Should accept current user context for personalized or privacy-filtered search.
- Should avoid returning private objects the current user cannot see.

SocialGraph:

- Should own friend/follow/block relationships.
- Must enforce user ownership and block semantics.
- Should expose relationship checks needed by other subgraphs only through stable APIs or future events/read models.

Recommendation:

- Should own ranking and recommendation generation.
- Should treat identity context as input, not authority for data ownership.
- Should avoid writing core social/media state.

Messaging:

- Must be protected by default.
- Must verify the current user is a participant before returning conversations or messages.
- Should avoid leaking participant metadata for conversations the user cannot access.

Notification:

- Must be protected by default.
- Should scope notification reads and mutations to `X-User-Id`.
- Should separate notification records from delivery channels.

Media:

- Should own media metadata, processing state, and upload/download authorization.
- Should not expose raw storage credentials.
- Prefer signed upload/download URLs with short TTL when storage integration exists.

## Testing Notes

Current verification includes 37 Authentication E2E assertions, 14 permanent Gateway tests, and the 31-test Backend-Payment baseline:

- Auth direct health/register/resend/verify/login flows.
- Auth direct session listing/history/logout/logoutAll/logoutSession.
- Auth direct refresh/change password/password reset.
- Internal `validateGatewaySession` success and wrong-secret failure.
- Gateway proxy health and public schema checks.
- Gateway proxy register/resend/verify/login.
- Gateway sets HttpOnly refresh cookie and strips raw refresh token values.
- Gateway validates sessions through Auth.
- Gateway rejects revoked sessions.
- Gateway refreshes with HttpOnly cookie.
- Gateway logout/logoutAll/logoutSession cookie behavior.
- Gateway rejects spoofed internal headers.
- Payment schema exposes `premiumPlans`, `premiumOrder`, and `createPremiumCheckout` while Auth payment/session fields remain internal.
- Payment Fusion forwards identity/session/correlation/Gateway secret without forwarding refresh cookies.
- PayOS proxy preserves raw bytes, strips browser/trusted spoofed headers, limits JSON bodies to 64 KiB, rate limits by IP, maps safe statuses, and returns 503 on network failure.

Permanent Gateway webhook proxy tests live in `fakebookGateway.Tests`. They cover raw-body preservation, trusted-header stripping, safe status mapping, input limits, network failure, and IP rate limiting.

Payment integration is composed from `Gateway/schema/Payment`. Use the `payment-fusion` client so Payment receives Gateway-owned identity, session, correlation, and secret headers without receiving the browser Authorization header or refresh token. The public PayOS endpoint is `POST /api/webhooks/payos`; it forwards to the configured `Subgraphs:Payment:WebhookUrl` using the dedicated `payment-webhook` client and never forwards browser authorization, cookies, refresh tokens, user/session headers, or caller-provided secrets.

## Known Work Left

- Add a script for schema export + Fusion compose.
- Add CI validation that `gateway.far` is in sync with committed source schemas.
- Add health/readiness endpoints outside GraphQL if deployment needs them.
- Add structured logging and metrics around subgraph latency/errors.
- Add rate limiting and query complexity policy if public traffic grows.
- Add configuration examples for every planned subgraph once they exist.
