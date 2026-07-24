# Provider authentication

Caliper supports four explicit model providers. OpenAI Platform and OpenAI Codex are separate
because they have different credentials, endpoints, catalogs, and billing/account semantics.

| Provider ID | Authentication | Default endpoint |
| --- | --- | --- |
| `OpenRouter` | API key | `https://openrouter.ai/api/v1` |
| `Gemini` | API key | `https://generativelanguage.googleapis.com/v1beta/openai/` |
| `OpenAI` | OpenAI Platform API key | `https://api.openai.com/v1` |
| `OpenAICodex` | ChatGPT OAuth (PKCE or device code) | `https://chatgpt.com/backend-api/codex` |

OpenAI Platform uses the Responses API. OpenAI Codex uses the Codex HTTP Responses transport;
Caliper still owns its agent loop, tool dispatch, permissions, retries, and persistence. The
Codex provider does not currently opt into the experimental WebSocket transport. At the Codex
provider boundary, Caliper moves the system prompt into the required top-level `instructions`,
forces stateless streaming (`store: false`), requests encrypted reasoning content for multi-turn
tool use, and omits generic sampling/output-limit fields that the subscription endpoint rejects.

## Console

```text
/providers
/auth status [provider]
/auth set-key <OpenRouter|Gemini|OpenAI>
/auth clear-key <OpenRouter|Gemini|OpenAI>
/auth login OpenAICodex
/auth login OpenAICodex --device-code
/auth logout OpenAICodex
/models [provider]
```

API-key input is hidden. The console stores provider credentials in
`~/.caliper/provider-auth.json`; on Unix the file is restricted to the current user. Environment
variables remain supported for API-key providers:

```text
CALIPER_OPENROUTER_KEY
CALIPER_GEMINI_KEY
CALIPER_OPENAI_KEY
```

## Desktop

Settings → Models & providers contains one section per provider. API keys and Codex access,
refresh, expiry, and account values use Windows Credential Manager. Codex sign-in opens the
system browser and returns through `http://localhost:1455/auth/callback`; sign-out removes every
stored Codex identity value. Values larger than one Windows credential blob, including Codex JWTs,
are split across versioned Credential Manager entries and reconstructed transparently.

## Manual acceptance pass

These checks require real credentials, browser interaction, or visual judgment and are
intentionally not part of the automated suite.

1. For each of `OpenRouter`, `Gemini`, and `OpenAI`, save a real key, select a compatible model,
   confirm `/providers` reports ready, load `/models <provider>`, and complete one prompt with a
   read-only tool call. Clear the saved key afterward if the machine is shared.
2. Run `/auth login OpenAICodex`, finish browser sign-in, confirm the callback page says it is
   safe to close, then verify `/auth status OpenAICodex`, `/models OpenAICodex`, and one prompt
   with a read-only tool call. Sign out and confirm status returns to signed out.
3. Repeat Codex sign-in with `--device-code` on a terminal where localhost callback handling is
   undesirable. OpenAI may disable this flow for some accounts; Caliper should report that and
   direct the user back to browser sign-in.
4. In the desktop app, verify all four provider sections at normal width and the 800×560 minimum,
   keyboard through the sign-in/sign-out controls, and confirm the account/status text and
   first-run card update after credentials change.
5. Confirm `~/.caliper/config.json` contains endpoints and provider selection but no API keys or
   OAuth tokens. On Windows, inspect Credential Manager for the Caliper entries; on Unix, confirm
   `~/.caliper/provider-auth.json` is readable only by its owner.
6. If testing token refresh, leave a signed-in Codex session running until the access token is
   near expiry and send another prompt. It should refresh without another browser login; a revoked
   refresh token should sign out and request authentication again.
