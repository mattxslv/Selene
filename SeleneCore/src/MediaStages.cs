using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Selene
{
    // Games > Download game media while it runs: art, songs and details side by side like Spotlight's stats (a small
    // label, the count, and a word for where it stands), instead of three lines of text
    internal class MediaStages
    {
        private class Stage { public string Label, Value, State; public bool Done; }
        private class Column { public TextBlock Value, State; }

        public readonly Grid Root = new Grid { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        private readonly List<Column> columns = new List<Column>();

        private static List<Stage> Read()
        {
            var list = new List<Stage>();

            int done, total;
            TrailerFetch.LibraryCounts(out done, out total);
            list.Add(new Stage
            {
                Label = "ART & TRAILERS", Value = total > 0 ? done + " / " + total : "–",
                State = total == 0 ? "Waiting" : TrailerFetch.Busy || done < total ? "Downloading" : "Done",
                Done = total > 0 && !TrailerFetch.Busy && done >= total
            });

            string value, state; bool finished;
            SongSetup.Stage(out value, out state, out finished);
            list.Add(new Stage { Label = "SONGS", Value = value, State = state, Done = finished });

            int planned = GameInfo.PlannedGames;
            list.Add(new Stage
            {
                Label = "DETAILS", Value = planned > 0 ? GameInfo.CheckedGames + " / " + planned : "All set",
                State = GameInfo.Busy ? "Filling in" : "Done", Done = !GameInfo.Busy
            });
            return list;
        }

        public MediaStages()
        {
            var stages = Read();
            for (int i = 0; i < stages.Count; i++)
            {
                if (i > 0)
                {
                    Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var line = new Border { Width = 1, Margin = new Thickness(26, 4, 26, 4), Background = Ink.Brush("SeleneInkColor", 0.15) };
                    Grid.SetColumn(line, Root.ColumnDefinitions.Count - 1);
                    Root.Children.Add(line);
                }
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var col = new StackPanel { MinWidth = 120 };
                col.Children.Add(new TextBlock { Text = stages[i].Label, FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55) });
                var v = new TextBlock { FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), Margin = new Thickness(0, 2, 0, 0) };
                var st = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Margin = new Thickness(0, 1, 0, 0) };
                col.Children.Add(v);
                col.Children.Add(st);
                Grid.SetColumn(col, Root.ColumnDefinitions.Count - 1);
                Root.Children.Add(col);
                columns.Add(new Column { Value = v, State = st });
            }
            Update(false);
        }

        public void Update() { Update(true); }

        private void Update(bool animate)
        {
            var stages = Read();
            for (int i = 0; i < stages.Count && i < columns.Count; i++)
            {
                var c = columns[i]; var s = stages[i];
                Set(c.Value, s.Value, animate);
                Set(c.State, (s.Done && s.State == "Done" ? "✓ " : "") + s.State, animate);
                c.State.Foreground = s.Done ? Ink.Brush("SeleneInkColor", 0.6) : Ink.Brush("GlyphColor");
            }
        }

        // a changed figure crossfades in (a small pop, not a jump)
        private static void Set(TextBlock t, string text, bool animate)
        {
            if (t.Text == text) return;
            t.Text = text;
            if (animate) t.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }
}
