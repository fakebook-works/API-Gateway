# Hướng Dẫn Agent Cho Fakebook API Gateway

File này là tài liệu làm việc cho developer và AI Agent khi chỉnh sửa Fakebook API Gateway hoặc tích hợp subgraph mới cho Fakebook.

Gateway là public GraphQL entry point cho frontend. Gateway compose nhiều GraphQL subgraph nhỏ bằng HotChocolate Fusion và forward request tới service phụ trách.

## Tổng Quan Dự Án

- Loại project: .NET 8 ASP.NET Core GraphQL Gateway.
- GraphQL stack: HotChocolate + HotChocolate Fusion.
- Endpoint public chính: `/graphql`.
- Route `/` redirect sang `/graphql`.
- Composition artifact: `fakebookGateway/gateway.far`.
- Subgraph hiện tại đã compose: `Authentication`, `Payment`.
- Subgraph dự kiến: `Search`, `SocialGraph`, `Recommendation`, `Messaging`, `Notification`, `Media`.
- Auth model: Gateway validate JWT local và validate active session với Authentication subgraph.
- Refresh token model: Gateway sở hữu browser cookie. Auth trả cookie instruction; Gateway apply instruction và scrub raw refresh token khỏi public GraphQL response.

## Gateway Có Thể Làm Gì

Capability hiện tại:

- Expose một public GraphQL endpoint duy nhất cho frontend.
- Load Fusion archive từ file với `.AddFileSystemConfiguration(...)`.
- Proxy GraphQL operations tới Authentication subgraph.
- Validate HS256 JWT access token bằng issuer, audience và signing key trong config.
- Validate session state của access token với Auth qua internal query `validateGatewaySession`.
- Cache kết quả Auth session validation trong một TTL ngắn.
- Strip trusted internal headers do browser gửi lên trước khi forward request xuống subgraph.
- Forward trusted identity context xuống subgraph:
  - `X-User-Id`
  - `X-Session-Id`
  - `X-Username`
  - `X-Correlation-ID`
  - `Authorization`
  - `X-Refresh-Token`
  - `X-Gateway-Secret`
- Đọc refresh token từ HttpOnly cookie đã cấu hình và forward xuống Auth bằng `X-Refresh-Token`.
- Consume Auth cookie instruction từ internal response header `X-Fakebook-Refresh-Cookie-Instruction`.
- Consume cookie instruction nếu frontend select nó trong GraphQL response payload.
- Set và clear browser refresh cookie.
- Set scalar field `refreshToken` thành null trong public GraphQL response.
- Set `GatewayCookieInstruction.value` thành null trong public GraphQL response.
- Thêm `X-Correlation-ID` vào response.
- Apply CORS cho frontend origins đã cấu hình với credentials enabled.
- Build và publish Docker image qua GitHub Actions workflow hiện có.

Giới hạn hiện tại:

- Authentication và Payment hiện đã được compose trong `gateway.far`.
- Permanent Payment webhook/Fusion tests nằm trong `fakebookGateway.Tests`.
- Fusion composition hiện là manual local workflow.
- Gateway chưa implement field-level authorization rule. Subgraph phải tự protect private operations bằng internal headers và `X-Gateway-Secret`, hoặc Gateway cần được mở rộng field policy trước khi expose sensitive fields từ subgraph yếu.
- Fusion URLs được bake vào `gateway.far` lúc compose. Runtime config `Subgraphs:*:Url` hiện dùng cho internal client của Gateway như Auth session validation, không phải generic service discovery cho mọi Fusion transport.

## Các File Quan Trọng

- `fakebookGateway/Program.cs`: đăng ký service, JWT auth, CORS, middleware order, load Fusion archive, map GraphQL endpoint.
- `fakebookGateway/Gateway/GatewayOptions.cs`: runtime options của Gateway và JWT.
- `fakebookGateway/Gateway/GatewayConstants.cs`: trusted internal headers, claim names và request item keys.
- `fakebookGateway/Gateway/GatewayEdgeMiddleware.cs`: strip trusted headers ở public edge, correlation id, session validation middleware.
- `fakebookGateway/Gateway/AuthSessionValidator.cs`: internal Auth call cho `validateGatewaySession`, có memory cache.
- `fakebookGateway/Gateway/FusionSubgraphHeaderHandler.cs`: outgoing subgraph HTTP handler, inject trusted headers và consume internal cookie instruction headers.
- `fakebookGateway/Gateway/GatewayCookieInstructionProcessor.cs`: apply cookie instruction `SET` và `CLEAR` lên browser response.
- `fakebookGateway/Gateway/GraphQlCookieResponseMiddleware.cs`: rewrite GraphQL response để scrub refresh token và cookie instruction values.
- `fakebookGateway/Gateway/schema/<SubgraphName>/schema.graphqls`: exported source schema của từng composed subgraph.
- `fakebookGateway/Gateway/schema/<SubgraphName>/schema-settings.json`: Fusion transport settings của từng source schema.
- `fakebookGateway/Gateway/schema/<SubgraphName>/schema-extensions.graphqls`: Gateway-owned source-schema extensions như `@internal`.
- `fakebookGateway/gateway.far`: composed Fusion archive được load lúc runtime.
- `fakebookGateway/appsettings.example.json`: safe runtime config example.
- `fakebookGateway/Dockerfile`: container build.
- `.github/workflows/docker-build.yml`: GHCR Docker build và push.

Không commit secret thật. JWT signing key, gateway shared secret, DB password, SMTP password và cloud credential phải nằm ngoài tracked config.

## Cấu Hình Runtime

Config section quan trọng:

```text
Jwt
Gateway
Subgraphs
```

JWT config bắt buộc:

```text
Jwt__Issuer
Jwt__Audience
Jwt__SigningKey
```

`Jwt:SigningKey` phải dài tối thiểu 32 bytes và phải trùng với signing key của Authentication subgraph, vì Auth issue access token và Gateway validate access token đó.

Gateway config bắt buộc:

```text
Gateway__InternalSharedSecret
```

Internal shared secret phải trùng với `Gateway:InternalSharedSecret` của Authentication subgraph. Nên dùng tối thiểu 32 bytes, dù Gateway hiện chỉ validate non-empty.

Environment variables hữu ích:

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

Giá trị mặc định:

```text
FusionArchivePath = gateway.far
AuthenticationGraphQLEndpoint = http://localhost:5001/graphql
SessionCacheSeconds = 30
RefreshTokenCookieName = fb_refresh
AllowedOrigins = http://localhost:3000, http://localhost:5173, http://localhost:5174
```

`Subgraphs__Authentication__Url` được internal Auth session validator của Gateway sử dụng. Fusion transport URL của Authentication nằm trong `Gateway/schema/Authentication/schema-settings.json` và được compose vào `gateway.far`.

## Middleware Order

Thứ tự hiện tại trong `Program.cs`:

```text
GatewayEdgeMiddleware
CORS
Authentication
Authorization
GatewaySessionValidationMiddleware
GraphQlCookieResponseMiddleware
MapGraphQL("/graphql")
```

Lý do thứ tự này quan trọng:

- `GatewayEdgeMiddleware` phải chạy sớm để strip browser-supplied trusted headers trước mọi auth/proxy logic.
- CORS chạy trước GraphQL để frontend credentialed requests hoạt động.
- JWT authentication phải chạy trước session validation.
- Session validation phải chạy trước GraphQL execution để invalid session bị reject tại Gateway.
- GraphQL cookie response middleware wrap GraphQL execution để apply cookie instruction và scrub sensitive fields.

## Public Request Flow

Unauthenticated public operation:

```text
Frontend -> Gateway /graphql
Gateway strip trusted headers
Gateway gán hoặc reuse X-Correlation-ID
Không có bearer token
Gateway forward operation qua Fusion
Subgraph quyết định operation đó có public hay không
Gateway scrub token/cookie values nếu response có
Gateway trả response về frontend
```

Authenticated operation:

```text
Frontend -> Gateway /graphql với Authorization: Bearer <accessToken>
Gateway strip trusted headers
Gateway validate JWT signature, issuer, audience, nbf, exp
Gateway lấy user_id, sid, username
Gateway gọi Auth validateGatewaySession với X-Gateway-Secret
Gateway cache kết quả session validation trong thời gian ngắn
Gateway lưu trusted identity context vào HttpContext.Items
Fusion forward request tới subgraph kèm internal headers
Subgraph resolve operation
Gateway apply cookie instruction nếu có
Gateway scrub raw refresh token values
Gateway trả response
```

Revoked session behavior:

- Auth access token có claim `sid`.
- Gateway gọi Auth để đảm bảo `sid` vẫn active.
- Nếu Auth trả invalid, Gateway respond HTTP 401 và GraphQL error code `UNAUTHENTICATED`.
- Positive validation có thể bị cache tối đa `Gateway:SessionCacheSeconds`, và bị cap bởi session expiry.
- Nếu cần revocation gần như immediate khi local test, giảm `Gateway__SessionCacheSeconds`.

## Internal Header Contract

Header do Gateway tạo:

```text
Authorization: Bearer <accessToken>
X-User-Id: <current-user-id>
X-Session-Id: <current-session-id>
X-Username: <current-username>
X-Correlation-ID: <request-correlation-id>
X-Refresh-Token: <raw-refresh-token-from-HttpOnly-cookie>
X-Gateway-Secret: <Gateway__InternalSharedSecret>
```

Quy tắc:

- Browser không được phép set trusted identity headers.
- Gateway strip `X-User-Id`, `X-Session-Id`, `X-Username`, `X-Gateway-Secret`, và `X-Refresh-Token` từ public request.
- Gateway tự tạo lại trusted headers trước khi gọi subgraph.
- Subgraph chỉ nên trust các header này khi `X-Gateway-Secret` hợp lệ và request đi qua trusted internal network path.
- Subgraph nên dùng `X-Correlation-ID` trong logs và outgoing calls.
- Non-Auth subgraph không đọc browser cookie và không xử lý refresh token.

## Refresh Token Và Cookie Flow

Auth sở hữu refresh token generation và rotation. Gateway sở hữu browser cookie.

Login:

```text
Frontend gọi login qua Gateway
Auth trả accessToken, refreshToken, refreshTokenCookie
Auth có thể trả X-Fakebook-Refresh-Cookie-Instruction header
Gateway set HttpOnly refresh cookie
Gateway set raw refreshToken thành null trong public response
Gateway set refreshTokenCookie.value thành null trong public response
Frontend nhận access token và user data
```

Refresh:

```text
Frontend gọi refreshToken qua Gateway, thường không cần input
Gateway đọc fb_refresh hoặc cookie name đã cấu hình
Gateway forward raw refresh token xuống Auth qua X-Refresh-Token
Auth rotate refresh token
Gateway set replacement cookie
Gateway trả access token mới
Gateway scrub raw refresh token values
```

Logout:

```text
Frontend gọi logout qua Gateway, thường không cần input
Gateway đọc refresh cookie và forward X-Refresh-Token
Auth revoke session
Gateway clear refresh cookie bằng instruction trả về
```

Logout all:

```text
Frontend gọi logoutAll với bearer access token
Gateway validate JWT/session
Auth revoke tất cả session của current user
Gateway clear current browser refresh cookie
```

Logout session:

```text
Frontend gọi logoutSession(sessionId)
Gateway validate JWT/session
Auth revoke target session
Nếu target là current session, Gateway clear current cookie
Nếu target là session khác, Gateway giữ nguyên current cookie
```

Implementation notes:

- `FusionSubgraphHeaderHandler` consume `X-Fakebook-Refresh-Cookie-Instruction` từ subgraph HTTP response và remove internal header đó.
- `GraphQlCookieResponseMiddleware` cũng scan GraphQL response JSON để tìm cookie instruction object.
- Bất kỳ scalar property nào tên `refreshToken` sẽ bị set null trong public response. Không tạo unrelated public field tên `refreshToken` trong subgraph về sau.
- Bất kỳ object nào có shape giống cookie instruction (`operation`, `name`, `path`, `maxAgeSeconds`) có thể trigger cookie processing. Không reuse shape này cho data khác.

## Public GraphQL Surface Hiện Tại

Public surface hiện tại đến từ Authentication và Payment.

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

Contract gender của Authentication:

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

`true` là Nam và `false` là Nữ. Output nullable để vẫn đọc được identity được tạo trước migration database.

Các field Auth nội bộ:

```text
validateGatewaySession
paymentPremiumState
setPaymentValidDate
```

Các field này tồn tại trong Authentication source schema nhưng được mark `@internal` trong Gateway schema extensions, nên không được xuất hiện trong public Gateway schema.

## Fusion Schema Layout

Mỗi subgraph có một folder:

```text
fakebookGateway/Gateway/schema/<SubgraphName>/
  schema.graphqls
  schema-settings.json
  schema-extensions.graphqls   optional
```

`schema.graphqls`:

- Exported source schema từ subgraph service.
- Cần được commit.
- Phải match version subgraph đang chạy.

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

Default khuyến nghị:

- Dùng `clientName: "fusion"` cho subgraph bình thường để `FusionSubgraphHeaderHandler` được apply.
- Chỉ dùng custom client name khi subgraph cần HTTP behavior riêng. Nếu dùng custom client, đăng ký trong `Program.cs` và add `FusionSubgraphHeaderHandler`.

`schema-extensions.graphqls`:

- Gateway-owned source schema extensions.
- Dùng để mark field `@internal`.
- Dùng cho composition-only metadata.
- Không đặt business schema vào đây trừ khi nó thật sự là Gateway-owned composition metadata.

Ví dụ:

```graphql
extend type Query {
  validateGatewaySession(input: GatewaySessionValidationInput!): GatewaySessionValidationPayload! @internal
}
```

## Compose Fusion Archive

Local tool cần có:

```powershell
dotnet tool install -g ChilliCream.Nitro.CommandLine --version 16.1.3
```

Compose từ folder `fakebookGateway`:

```powershell
cd .\fakebookGateway
nitro fusion compose `
  --source-schema-file .\Gateway\schema\Authentication `
  --archive .\gateway.far `
  --env Development
```

Khi có nhiều subgraph, include tất cả source schema folder:

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

Compose production:

```powershell
nitro fusion compose `
  --source-schema-file .\Gateway\schema\Authentication `
  --archive .\gateway.far `
  --env Production
```

Sau khi compose:

- Commit `schema.graphqls`, `schema-settings.json`, `schema-extensions.graphqls`, và `gateway.far` đã update.
- Build Gateway.
- Chạy smoke test Auth và Gateway.
- Verify public schema không expose field bị mark `@internal`.

## Lệnh Local

Build:

```powershell
dotnet build .\fakebookGateway\fakebookGateway.csproj --no-restore
```

Run:

```powershell
dotnet run --project .\fakebookGateway\fakebookGateway.csproj
```

Ví dụ local run với Auth port `5001` và Gateway port `5099`:

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

## Cách Implement Subgraph Mới

Dùng checklist này khi thêm `Search`, `SocialGraph`, `Recommendation`, `Messaging`, `Notification`, `Media`, hoặc subgraph mới về sau.

### 1. Định Nghĩa Ownership

Trước khi code, cần chốt bounded context:

- Subgraph sở hữu data nào?
- Subgraph sở hữu database/schema nào?
- GraphQL fields nào thuộc subgraph?
- Operation nào public?
- Operation nào cần authenticated user?
- Operation nào cần ownership/permission check cụ thể?
- Subgraph cần event hoặc call nào từ service khác?

Không duplicate ownership:

- Authentication sở hữu identity, credentials, sessions, OTP, token issuing.
- SocialGraph nên sở hữu friend/follow/block relationships.
- Media nên sở hữu media metadata, upload state, access decisions cho media.
- Messaging nên sở hữu conversations, participants, messages, read state.
- Notification nên sở hữu notification records và delivery state.
- Search nên sở hữu searchable indexes và search behavior.
- Recommendation nên sở hữu ranking inputs, feature retrieval và recommendations.

### 2. Tạo Subgraph Service

Baseline khuyến nghị cho .NET subgraph:

- .NET 8 ASP.NET Core.
- HotChocolate GraphQL.
- `HotChocolate.AspNetCore.CommandLine` để export schema.
- Schema name rõ ràng và trùng với subgraph name.
- Endpoint `/graphql`.
- Correlation id middleware dùng `X-Correlation-ID`.
- Internal gateway secret validation.
- Structured errors với stable `extensions.code`.

GraphQL setup tối thiểu:

```csharp
builder.Services
    .AddGraphQLServer("Search")
    .AddQueryType<Query>()
    .AddMutationType<Mutation>();

app.MapGraphQL();
app.RunWithGraphQLCommands(args);
```

Schema name phải stable vì export command và Fusion composition có thể refer tới nó.

### 3. Validate Gateway Header Trong Subgraph

Mỗi subgraph nên reject direct browser access cho protected operations.

Behavior khuyến nghị:

- Require `X-Gateway-Secret` cho internal-only subgraph.
- Với public operations, vẫn có thể require `X-Gateway-Secret` vì public client nên gọi qua Gateway.
- Với protected operations, require valid `X-Gateway-Secret`, `X-User-Id`, và thường cả `X-Session-Id`.
- Dùng `X-User-Id` làm current user id authoritative.
- Dùng `X-Username` chỉ cho display/context, không dùng làm authority.
- Chỉ dùng `Authorization` nếu subgraph có chủ đích validate JWT riêng.
- Không trust user id hoặc session id do browser gửi trực tiếp.

Context object gợi ý:

```csharp
public sealed record GatewayUserContext(
    long? UserId,
    long? SessionId,
    string? Username,
    string CorrelationId);
```

Resolver pattern gợi ý:

```csharp
public async Task<IReadOnlyList<ResultType>> MyProtectedData(
    [Service] IGatewayUserContextAccessor contextAccessor,
    CancellationToken cancellationToken)
{
    var user = contextAccessor.RequireUser();
    return await service.GetForUserAsync(user.UserId, cancellationToken);
}
```

Dùng GraphQL errors như:

```text
UNAUTHENTICATED
FORBIDDEN
INVALID_INPUT
NOT_FOUND
CONFLICT
RATE_LIMITED
```

### 4. Thiết Kế GraphQL Schema Thân Thiện Với Gateway

Quy tắc chung:

- Schema phải do subgraph sở hữu.
- Ưu tiên field rõ ràng, stable, không leak database table shape.
- Dùng `Long` cho Fakebook Snowflake IDs trừ khi có lý do mạnh để expose `ID`.
- Dùng `DateTime` có offset cho timestamp.
- Dùng `Date` cho calendar-only value.
- Không expose raw secrets, internal tokens, provider credentials, storage keys.
- Không tạo public field tên `refreshToken` ngoài Authentication vì Gateway scrub scalar `refreshToken`.
- Không tạo object shape vô tình giống `GatewayCookieInstruction`.
- Mutation payload nên consistent: có `success`, `message`, và object thay đổi nếu hữu ích.
- Dùng cursor pagination cho list không giới hạn.
- Tránh response nested quá lớn gây cross-service join đắt tiền.
- Dùng explicit error codes trong GraphQL errors.

Naming khuyến nghị:

```text
Query.searchPosts
Query.myFriends
Query.myNotifications
Query.conversation
Mutation.sendMessage
Mutation.markNotificationRead
Mutation.createPostMediaUpload
```

Tránh generic names để collide giữa subgraphs:

```text
Query.items
Query.list
Mutation.create
Mutation.update
```

### 5. Export Schema Của Subgraph

Với HotChocolate subgraph có command-line support:

```powershell
dotnet run --project <path-to-subgraph.csproj> --no-build -- schema export --schema-name <SubgraphName> --output <gateway-repo>\fakebookGateway\Gateway\schema\<SubgraphName>\schema.graphqls
```

Ví dụ:

```powershell
dotnet run --project ..\Backend-Search\fakebookSearch\fakebookSearch.csproj --no-build -- schema export --schema-name Search --output .\fakebookGateway\Gateway\schema\Search\schema.graphqls
```

Nếu subgraph không dùng HotChocolate:

- Export hoặc viết valid GraphQL SDL file.
- Lưu vào `fakebookGateway/Gateway/schema/<SubgraphName>/schema.graphqls`.
- Đảm bảo custom scalars và directives được define.

### 6. Thêm Fusion Settings

Tạo:

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

Chỉ dùng custom `clientName` khi cần:

```json
"clientName": "search-fusion"
```

Sau đó đăng ký trong `Program.cs`:

```csharp
builder.Services
    .AddHttpClient("search-fusion")
    .AddHttpMessageHandler<FusionSubgraphHeaderHandler>();
```

### 7. Thêm Schema Extensions Nếu Cần

Tạo:

```text
fakebookGateway/Gateway/schema/<SubgraphName>/schema-extensions.graphqls
```

Dùng cho Gateway composition metadata:

```graphql
extend type Query {
  internalDebugField: String! @internal
}
```

Use cases thường gặp:

- Hide internal fields bằng `@internal`.
- Thêm Fusion metadata cần cho composition.
- Xử lý naming/ownership concerns mà không cần đổi generated source schema.

Không dùng schema extensions để che lỗi security. Nếu field sensitive và không bao giờ được reachable, hãy remove khỏi subgraph schema hoặc enforce authorization trong subgraph.

### 8. Compose Gateway Archive

Chạy `nitro fusion compose` từ `fakebookGateway` và include mọi subgraph folder. Composition phải include tất cả source schemas, không chỉ subgraph mới.

```powershell
cd .\fakebookGateway
nitro fusion compose `
  --source-schema-file .\Gateway\schema\Authentication `
  --source-schema-file .\Gateway\schema\Search `
  --archive .\gateway.far `
  --env Development
```

### 9. Cấu Hình Local Ports

Chọn port local ổn định để tránh collision. Gợi ý:

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

Update Development URLs trong mỗi `schema-settings.json`, sau đó recompose `gateway.far`.

### 10. Test Qua Gateway

Minimum checks cho mỗi subgraph mới:

- Subgraph start và direct `health` response ok.
- Gateway start với `gateway.far` mới compose.
- Public schema của Gateway có intended fields.
- Public schema của Gateway ẩn `@internal` fields.
- Public operations hoạt động không cần bearer token chỉ khi có chủ đích public.
- Protected operations fail khi không có bearer token.
- Protected operations pass với Auth-issued access token hợp lệ.
- Protected operations fail sau logout/revoked session.
- Browser-supplied `X-User-Id`/`X-Gateway-Secret` spoofing không bypass được auth.
- `X-Correlation-ID` tới được subgraph logs.
- Subgraph nhận `X-User-Id`, `X-Session-Id`, và `X-Username` cho authenticated calls.

## Guideline Phát Triển Subgraph

Security:

- Coi Gateway là public edge, không phải security layer duy nhất.
- Require `X-Gateway-Secret` cho protected subgraph operations.
- Require `X-User-Id` cho user-scoped reads và mutations.
- Check resource ownership bên trong owning subgraph.
- Không trust IDs trong GraphQL input như authority.
- Non-Auth subgraph không gọi trực tiếp Auth database.
- Non-Auth subgraph không nhận refresh token.
- Không log tokens, secrets, OTPs, passwords, cookies, hoặc credential-bearing URLs.

Reliability:

- Propagate `X-Correlation-ID`.
- Dùng cancellation token trong resolvers và data access.
- Mutation nên idempotent nếu product behavior cho phép.
- Dùng stable error codes.
- Đặt timeout cho outbound calls.
- Tránh cross-subgraph synchronous chains trong hot path.

Schema quality:

- Định nghĩa ownership rõ cho mọi type và field.
- Input phải explicit và validated.
- Dùng pagination cho lists.
- Payload phải predictable.
- Ưu tiên additive schema changes.
- Tránh rename field sau khi frontend đã integrate nếu chưa coordinate.
- Tránh duplicate root fields giữa subgraphs.

Data consistency:

- Mỗi subgraph sở hữu database writes của mình.
- Cross-service data nên reference bằng IDs.
- Với denormalized read model, cần rõ source of truth và refresh strategy.
- Dùng events/outbox về sau nếu workflow cần async cross-service consistency.

Testing:

- Thêm direct subgraph tests cho business rules.
- Thêm Gateway proxy tests cho composed behavior.
- Test unauthorized, unauthenticated, và revoked-session paths.
- Test schema composition sau mỗi schema change.

## Ghi Chú Cho Các Subgraph Dự Kiến

Authentication:

- Đã implement.
- Sở hữu identity, credentials, sessions, OTP, JWT issuing, refresh token rotation, cookie instruction contract.
- Expose `validateGatewaySession` cho Gateway internal use.

Search:

- Nên sở hữu search indexes và query behavior.
- Không sở hữu source content.
- Nên nhận current user context cho personalized hoặc privacy-filtered search.
- Không return private objects mà current user không được xem.

SocialGraph:

- Nên sở hữu friend/follow/block relationships.
- Phải enforce user ownership và block semantics.
- Nên expose relationship checks mà subgraph khác cần qua stable APIs hoặc future events/read models.

Recommendation:

- Nên sở hữu ranking và recommendation generation.
- Treat identity context như input, không phải authority cho data ownership.
- Không write core social/media state.

Messaging:

- Protected by default.
- Phải verify current user là participant trước khi return conversations/messages.
- Không leak participant metadata của conversation mà user không có quyền access.

Notification:

- Protected by default.
- Scope notification reads và mutations theo `X-User-Id`.
- Tách notification records khỏi delivery channels.

Media:

- Nên sở hữu media metadata, processing state, upload/download authorization.
- Không expose raw storage credentials.
- Khi có storage integration, ưu tiên signed upload/download URLs với TTL ngắn.

## Testing Notes

Verification hiện tại gồm 37 Authentication E2E assertions, 14 permanent Gateway tests và baseline 31 tests của Backend-Payment:

- Auth direct health/register/resend/verify/login flows.
- Auth direct session listing/history/logout/logoutAll/logoutSession.
- Auth direct refresh/change password/password reset.
- Internal `validateGatewaySession` success và wrong-secret failure.
- Gateway proxy health và public schema checks.
- Gateway proxy register/resend/verify/login.
- Gateway set HttpOnly refresh cookie và strip raw refresh token values.
- Gateway validate session thông qua Auth.
- Gateway reject revoked sessions.
- Gateway refresh bằng HttpOnly cookie.
- Gateway logout/logoutAll/logoutSession cookie behavior.
- Gateway reject spoofed internal headers.
- Payment schema expose `premiumPlans`, `premiumOrder`, `createPremiumCheckout`, đồng thời Auth payment/session fields vẫn internal.
- Payment Fusion forward identity/session/correlation/Gateway secret nhưng không forward refresh cookie.
- PayOS proxy giữ nguyên raw bytes, strip browser/trusted spoofed headers, giới hạn JSON body 64 KiB, rate limit theo IP, map safe status và trả 503 khi network failure.

Permanent Gateway webhook proxy tests nằm trong `fakebookGateway.Tests`. Tests cover raw-body preservation, trusted-header stripping, safe status mapping, input limits, network failure và IP rate limiting.

Payment được compose từ `Gateway/schema/Payment`. Dùng client `payment-fusion` để Payment nhận identity, session, correlation và secret do Gateway tạo nhưng không nhận browser Authorization header hay refresh token. Public PayOS endpoint là `POST /api/webhooks/payos`; route này forward tới `Subgraphs:Payment:WebhookUrl` bằng client riêng `payment-webhook` và không bao giờ forward browser authorization, cookies, refresh token, user/session headers hay secret do caller cung cấp.

## Việc Nên Làm Tiếp

- Thêm script export schema + Fusion compose.
- Thêm CI validation để đảm bảo `gateway.far` sync với committed source schemas.
- Thêm health/readiness endpoints ngoài GraphQL nếu deployment cần.
- Thêm structured logging và metrics cho subgraph latency/errors.
- Thêm rate limiting và query complexity policy nếu public traffic tăng.
- Thêm config examples cho mọi planned subgraph khi chúng tồn tại.
