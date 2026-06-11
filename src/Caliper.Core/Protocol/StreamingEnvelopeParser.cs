// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;

namespace Caliper.Core.Protocol;

/// <summary>
/// Single-pass state machine that extracts "rationale" and "content" string values
/// as incremental deltas from a streaming (possibly partial) constrained JSON envelope.
/// Call <see cref="Complete"/> after the stream ends to get the fully-validated turn.
/// </summary>
public sealed class StreamingEnvelopeParser
{
    private enum S
    {
        Scan,           // scanning for '"' to start a key
        InKey,          // inside key string chars
        KeyEsc,         // after '\' in key
        AfterKey,       // after closing '"' of key, waiting for ':'
        BeforeValue,    // after ':', waiting for value start
        InTracked,      // inside tracked string value (thought / content)
        TrackedEsc,     // after '\' in tracked value
        TrackedUni,     // after '\u' in tracked value — collecting hex digits
        InUntracked,    // inside untracked string value
        UntrackedEsc,   // after '\' in untracked value
        UntrackedUni,   // after '\u' in untracked value
        InSimple,       // inside non-string value (number / bool / null)
    }

    private S _s = S.Scan;
    private readonly StringBuilder _buf  = new();   // full accumulated text → Complete()
    private readonly StringBuilder _key  = new();   // current key name
    private readonly char[] _hexBuf      = new char[4];
    private int _hexCount;
    private EnvelopeField _field;
    private int _depth;      // nesting depth (> 0 = inside nested {…} or […])
    private bool _dStr;      // in a JSON string while at depth > 0
    private bool _dEsc;      // after '\' while in a string at depth > 0
    private char? _pendingHighSurrogate;

    /// <summary>
    /// Push incoming token text; returns 0..N field-scoped deltas with decoded text.
    /// </summary>
    public IEnumerable<EnvelopeDelta> Push(ReadOnlySpan<char> tokenDelta)
    {
        var result = new List<EnvelopeDelta>();
        foreach (var c in tokenDelta)
        {
            _buf.Append(c);
            if (_depth > 0)
                ProcDepth(c);
            else
                ProcTop(c, result);
        }
        return result;
    }

    /// <summary>
    /// Deserialize the fully-accumulated buffer into an <see cref="AgentTurn"/>.
    /// Throws if the buffer is not valid JSON or does not match the schema.
    /// </summary>
    public AgentTurn Complete()
    {
        return AgentTurnParser.Parse(_buf.ToString());
    }

    /// <summary>Reset the parser so it can be reused for the next turn.</summary>
    public void Reset()
    {
        _s      = S.Scan;
        _depth  = 0;
        _dStr   = false;
        _dEsc   = false;
        _hexCount = 0;
        _pendingHighSurrogate = null;
        _buf.Clear();
        _key.Clear();
    }

    // ── depth > 0 ──────────────────────────────────────────────────────────────
    private void ProcDepth(char c)
    {
        if (_dEsc) { _dEsc = false; return; }
        if (_dStr)
        {
            if (c == '\\') _dEsc = true;
            else if (c == '"') _dStr = false;
        }
        else
        {
            switch (c)
            {
                case '"':       _dStr = true;  break;
                case '{' or '[': _depth++;      break;
                case '}' or ']': _depth--;      break;
            }
        }
    }

    // ── depth == 0 ─────────────────────────────────────────────────────────────
    private void ProcTop(char c, List<EnvelopeDelta> result)
    {
        switch (_s)
        {
            case S.Scan:
                if (c == '"') { _s = S.InKey; _key.Clear(); }
                break;

            case S.InKey:
                if (c == '\\')      _s = S.KeyEsc;
                else if (c == '"')  _s = S.AfterKey;
                else                _key.Append(c);
                break;

            case S.KeyEsc:
                _key.Append(c);   // simple escape in a key; just keep the char
                _s = S.InKey;
                break;

            case S.AfterKey:
                if (c == ':') _s = S.BeforeValue;
                break;

            case S.BeforeValue:
                if (c == '"')
                {
                    var key = _key.ToString();
                    if (key == "rationale")    { _field = EnvelopeField.Rationale; _s = S.InTracked; }
                    else if (key == "content") { _field = EnvelopeField.Content;   _s = S.InTracked; }
                    else                        _s = S.InUntracked;
                }
                else if (c is '{' or '[') { _depth = 1; _dStr = false; _dEsc = false; _s = S.Scan; }
                else if (c is not (' ' or '\t' or '\r' or '\n')) _s = S.InSimple;
                break;

            case S.InTracked:
                if (c == '\\')     _s = S.TrackedEsc;
                else if (c == '"') { FlushPendingHighSurrogate(result); _s = S.Scan; }
                else
                {
                    FlushPendingHighSurrogate(result);
                    result.Add(new EnvelopeDelta(_field, c.ToString()));
                }
                break;

            case S.TrackedEsc:
                switch (c)
                {
                    case 'u':
                        _hexCount = 0;
                        _s = S.TrackedUni;
                        break;
                    case '"':
                        EmitEscaped("\"", result);
                        break;
                    case '\\':
                        EmitEscaped("\\", result);
                        break;
                    case '/':
                        EmitEscaped("/", result);
                        break;
                    case 'n':
                        EmitEscaped("\n", result);
                        break;
                    case 'r':
                        EmitEscaped("\r", result);
                        break;
                    case 't':
                        EmitEscaped("\t", result);
                        break;
                    case 'b':
                        EmitEscaped("\b", result);
                        break;
                    case 'f':
                        EmitEscaped("\f", result);
                        break;
                    default:
                        EmitEscaped(c.ToString(), result);
                        break;
                }
                break;

            case S.TrackedUni:
                _hexBuf[_hexCount++] = c;
                if (_hexCount == 4)
                {
                    if (TryReadHexCodeUnit(out var codeUnit))
                        EmitUnicodeCodeUnit(codeUnit, result);
                    _s = S.InTracked;
                }
                break;

            case S.InUntracked:
                if (c == '\\')     _s = S.UntrackedEsc;
                else if (c == '"') _s = S.Scan;
                break;

            case S.UntrackedEsc:
                _s = c == 'u' ? S.UntrackedUni : S.InUntracked;
                if (c == 'u') _hexCount = 0;
                break;

            case S.UntrackedUni:
                if (++_hexCount == 4) _s = S.InUntracked;
                break;

            case S.InSimple:
                if (c is ',' or '}' or ']' or ' ' or '\t' or '\n' or '\r')
                    _s = S.Scan;
                break;
        }
    }

    private void EmitEscaped(string text, List<EnvelopeDelta> result)
    {
        _s = S.InTracked;
        FlushPendingHighSurrogate(result);
        result.Add(new EnvelopeDelta(_field, text));
    }

    private bool TryReadHexCodeUnit(out char codeUnit)
    {
        var value = 0;
        for (var i = 0; i < _hexCount; i++)
        {
            var digit = FromHex(_hexBuf[i]);
            if (digit < 0)
            {
                codeUnit = '\0';
                _pendingHighSurrogate = null;
                return false;
            }

            value = (value << 4) + digit;
        }

        codeUnit = (char)value;
        return true;
    }

    private void EmitUnicodeCodeUnit(char codeUnit, List<EnvelopeDelta> result)
    {
        if (_pendingHighSurrogate is { } high)
        {
            if (char.IsLowSurrogate(codeUnit))
                result.Add(new EnvelopeDelta(_field, new string(new[] { high, codeUnit })));
            else
            {
                result.Add(new EnvelopeDelta(_field, high.ToString()));
                result.Add(new EnvelopeDelta(_field, codeUnit.ToString()));
            }

            _pendingHighSurrogate = null;
            return;
        }

        if (char.IsHighSurrogate(codeUnit))
        {
            _pendingHighSurrogate = codeUnit;
            return;
        }

        result.Add(new EnvelopeDelta(_field, codeUnit.ToString()));
    }

    private void FlushPendingHighSurrogate(List<EnvelopeDelta> result)
    {
        if (_pendingHighSurrogate is not { } high)
            return;

        result.Add(new EnvelopeDelta(_field, high.ToString()));
        _pendingHighSurrogate = null;
    }

    private static int FromHex(char c) =>
        c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
}
