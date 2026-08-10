/**
 * D-184: length is the whole password policy — no composition rules. The server enforces the
 * same floor in `RegisterWithPasswordUseCase`; this constant exists so the client's inline
 * validation cannot drift away from it field by field.
 */
export const PASSWORD_MIN_LENGTH = 12
