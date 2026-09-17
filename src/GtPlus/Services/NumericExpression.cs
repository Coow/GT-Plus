using System;
using System.Globalization;

namespace GtPlus.Services;

/// <summary>arithmetic for the numeric property boxes: "+ - * /" with the usual precedence and parentheses.
/// Text that opens with an operator continues from the value already in the box, so "*2" doubles it and
/// "-5" takes five off it - which is why a typed "-5" is a subtraction here and never the number minus five.
/// Half-typed text ("*", "2+") is not an error, just not a complete expression yet, and the caller leaves the value alone</summary>
public static class NumericExpression
{
    /// <summary>true when <paramref name="text"/> opens with an operator and therefore continues from the box's value</summary>
    public static bool IsRelative(string? text)
    {
        var s = text?.TrimStart();
        return !string.IsNullOrEmpty(s) && s[0] is '+' or '-' or '*' or '/';
    }

    /// <summary>evaluates <paramref name="text"/>, continuing from <paramref name="current"/> when it opens with an
    /// operator; false when the text is not a complete expression, divides by zero, or overflows</summary>
    public static bool TryEvaluate(string? text, decimal current, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parser = new Parser(text.Trim());
        try
        {
            var value = parser.Expression(IsRelative(text) ? current : null);
            if (!parser.AtEnd) return false;
            result = value;
            return true;
        }
        catch (FormatException)        { return false; }
        catch (DivideByZeroException)  { return false; }
        catch (OverflowException)      { return false; }
    }

    /// <summary>recursive descent over "+ - * / ( )" and decimal literals; a malformed or unfinished
    /// expression throws <see cref="FormatException"/> and <see cref="TryEvaluate"/> turns that into false</summary>
    private sealed class Parser
    {
        private readonly string _s;
        private readonly char   _decimalSeparator;
        private int _i;

        public Parser(string s)
        {
            _s = s;
            var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            _decimalSeparator = sep.Length == 1 ? sep[0] : '.';
        }

        public bool AtEnd { get { SkipWhite(); return _i >= _s.Length; } }

        /// <summary>the + - level; <paramref name="seed"/> stands in for the leading operand when the text opens with an operator</summary>
        public decimal Expression(decimal? seed)
        {
            var left = Term(seed);
            while (true)
            {
                SkipWhite();
                var c = Peek();
                if      (c == '+') { _i++; left += Term(null); }
                else if (c == '-') { _i++; left -= Term(null); }
                else return left;
            }
        }

        private decimal Term(decimal? seed)
        {
            var left = seed ?? Factor();
            while (true)
            {
                SkipWhite();
                var c = Peek();
                if (c == '*') { _i++; left *= Factor(); }
                else if (c == '/')
                {
                    _i++;
                    var divisor = Factor();
                    if (divisor == 0m) throw new DivideByZeroException();
                    left /= divisor;
                }
                else return left;
            }
        }

        private decimal Factor()
        {
            SkipWhite();
            switch (Peek())
            {
                case '+': _i++; return Factor();
                case '-': _i++; return -Factor();
                case '(':
                    _i++;
                    var inner = Expression(null);
                    SkipWhite();
                    if (Peek() != ')') throw new FormatException();
                    _i++;
                    return inner;
                default:
                    return Number();
            }
        }

        /// <summary>a run of digits with at most one decimal point; both '.' and the current culture's separator
        /// are accepted so the box parses back exactly what it printed, whatever the machine's regional settings</summary>
        private decimal Number()
        {
            var start = _i;
            var seenPoint = false;
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (char.IsAsciiDigit(c)) { _i++; continue; }
                if ((c == '.' || c == _decimalSeparator) && !seenPoint) { seenPoint = true; _i++; continue; }
                break;
            }

            if (_i == start) throw new FormatException();

            var token = _s[start.._i].Replace(_decimalSeparator, '.');
            if (!decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                throw new FormatException();
            return value;
        }

        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private void SkipWhite()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }
    }
}
