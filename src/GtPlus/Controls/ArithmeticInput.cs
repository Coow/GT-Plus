using System;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GtPlus.Services;

namespace GtPlus.Controls;

/// <summary>lets a <see cref="NumericUpDown"/> take arithmetic where it normally takes a number: "100/3", "(2+3)*4",
/// and - the point of it - an expression that opens with an operator and so continues from what is already in the box,
/// "*2" to double it or "-5" to take five off. Plain numbers still parse exactly as they did before this was attached.
/// <para>The box parses on every keystroke, so the value follows the expression live; the operand it continues from is
/// captured when editing starts and held until the edit is committed, otherwise each keystroke would compound the last.</para></summary>
public static class ArithmeticInput
{
    public const string Tip = "Takes arithmetic: *2 doubles, /2 halves, +10 and -10 nudge, or type a sum like (100+20)/2";

    /// <summary>wires <paramref name="nud"/> for arithmetic input and gives it the explanatory tooltip</summary>
    public static void Attach(NumericUpDown nud)
    {
        _ = new Behavior(nud);
        ToolTip.SetTip(nud, Tip);
    }

    /// <summary>the box's text/value converter plus the focus bookkeeping that decides what "*2" is twice of</summary>
    private sealed class Behavior : IValueConverter
    {
        private readonly NumericUpDown _nud;

        private decimal? _base;             // what an expression continues from, captured while the box is being edited
        private decimal? _lastConverted;    // the value the last successful parse produced
        private string?  _lastExpression;   // and the text it came from, when that text was an expression

        public Behavior(NumericUpDown nud)
        {
            _nud = nud;
            nud.TextConverter = this;

            nud.GotFocus  += (_, _) => _base = nud.Value ?? 0m;
            nud.LostFocus += (_, _) => { _base = null; _lastExpression = null; };

            // the spinner, a label scrub or the panel repopulating all move the value under an open edit;
            // the expression has to continue from where the box actually is now, not where it was on focus
            nud.ValueChanged += (_, e) =>
            {
                if (_base is not null && e.NewValue != _lastConverted) _base = e.NewValue ?? 0m;
            };

            // Enter commits without redrawing the text, so "*2" would sit there over a value of 200
            nud.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        private decimal Current => _base ?? _nud.Value ?? 0m;

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _lastExpression is null || _nud.Text != _lastExpression) return;

            // posted: the control commits the text in its own handler for this same key press
            Dispatcher.UIThread.Post(() =>
            {
                if (_lastExpression is null || _nud.Text != _lastExpression) return;
                _lastExpression = null;
                _base = _nud.Value ?? 0m;
                _nud.Text = Format(_nud.Value);
            });
        }

        private string? Format(decimal? value)
        {
            var format = _nud.FormatString;
            if (!string.IsNullOrEmpty(format) && format.Contains("{0"))
                return string.Format(_nud.NumberFormat, format, value);
            return value?.ToString(format, _nud.NumberFormat);
        }

        /// <summary>text to value; throws the way the control's own parser does when the text is not a number yet,
        /// which leaves the value alone and the half-typed text on screen</summary>
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value as string;
            if (string.IsNullOrEmpty(text)) return null;

            // a plain number goes through the control's own rules, untouched: same number styles, same
            // out-of-range handling. Only text opening with an operator is ours, "-5" included
            if (!NumericExpression.IsRelative(text) &&
                decimal.TryParse(text, _nud.ParsingNumberStyle, _nud.NumberFormat, out var plain))
            {
                _lastExpression = null;
                _lastConverted  = plain;
                return plain;
            }

            if (!NumericExpression.TryEvaluate(text, Current, out var result))
                throw new InvalidDataException("Input string was not in a correct format.");

            // a computed result the user could not see coming is pinned to the box's range rather than
            // thrown away as out of range, which is what the label scrub does with a drag that runs too far
            result = Math.Clamp(result, _nud.Minimum, _nud.Maximum);

            _lastExpression = text;
            _lastConverted  = result;
            return result;
        }

        /// <summary>value to text; the control hands the whole job to the converter once one is set, so this has to
        /// reproduce what <see cref="NumericUpDown.FormatString"/> would have done on its own</summary>
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Format(value as decimal?);
    }
}
