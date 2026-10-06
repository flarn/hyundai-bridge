# Engineering rules

Implement only requested behavior. Inspect existing code before introducing a pattern.
Prefer simple, explicit code and focused diffs. Test changed behavior; no stubs or speculative abstractions.

This integration targets Hyundai in Europe generally, not one model. Keep Hyundai API/authentication details inside `src/HyundaiBridge/Hyundai`.
The versioned MQTT contract is the boundary: Home Assistant must know only the normalized contract and expose native entities, without MQTT Discovery.

Work in the phases documented in `docs/implementation-plan.md`. Distinguish local tests, upstream reports, actual Hyundai verification and deployment.
Never log credentials, tokens, authentication response bodies or precise location. Never call a vehicle command successful merely because submission was accepted.
