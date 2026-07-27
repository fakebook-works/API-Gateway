# API Gateway agent rules

When this repo is inside the Fakebook workspace, also read the workspace AGENTS.md and
docs/api-security-contract.md. The rules below remain mandatory in a standalone clone.

- Gateway is the only browser GraphQL edge. Do not expose or proxy arbitrary subgraph APIs.
- Strip all browser-supplied trusted/internal headers before authentication.
- Validate RS256 issuer, audience, lifetime, algorithm and kid with the public key only.
- Validate the live Auth session and preserve SSE session revalidation.
- Keep rate, parser, depth, cycle, planner, timeout and concurrency limits enabled.
- Use a distinct configured secret for every subgraph; never restore the shared fallback.
- Refresh tokens stay in the scoped HttpOnly cookie flow and are scrubbed from public data.
- New public REST routes require content-type/body limits, rate limiting and negative tests.
- PayOS webhook forwarding preserves exact body bytes and never forwards browser auth,
  cookies or trusted headers.
- A schema change must export the subgraph schema, recompose Fusion and run Gateway tests.

Run dotnet test fakebookGateway.sln before handoff. Do not weaken a security test to make a
new operation compose.
