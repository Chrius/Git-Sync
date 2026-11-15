using System.Drawing;
using System.Media;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// MessageBox in the app's dark colors. The system MessageBox ignores
/// Application.SetColorMode and always comes out light, in the middle of dark
/// windows. Same arguments and answers as MessageBox.Show, so a call only
/// changes its class name.
/// </summary>
internal static class DarkMessageBox
{
    internal static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) =>
        Show(null, text, caption, buttons, icon);

    internal static DialogResult Show(
        IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        using var form = new MessageForm(text, caption, buttons, icon, defaultButton, owned: owner is not null);

        // The sound the system box would make, so a warning still sounds like one.
        SoundOf(icon)?.Play();

        return owner is null ? form.ShowDialog() : form.ShowDialog(owner);
    }

    private static SystemSound? SoundOf(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Error => SystemSounds.Hand,
        MessageBoxIcon.Warning => SystemSounds.Exclamation,
        MessageBoxIcon.Question => SystemSounds.Question,
        MessageBoxIcon.Information => SystemSounds.Asterisk,
        _ => null
    };

    private sealed class MessageForm : Form
    {
        private const int Edge = 20;
        private const int IconSize = 32;
        private const int TextGap = 14;
        private const int MaxTextWidth = 420;
        private const int ButtonWidth = 88;
        private const int ButtonHeight = 28;
        private const int ButtonGap = 8;

        internal MessageForm(
            string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
            MessageBoxDefaultButton defaultButton, bool owned)
        {
            Text = caption;
            ShowIcon = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            // Without an owner (the database error at startup) it is the only
            // window there is, so it needs its taskbar button.
            ShowInTaskbar = !owned;
            StartPosition = owned ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9F);

            Icon? picture = IconOf(icon);
            int textLeft = picture is null ? Edge : Edge + IconSize + TextGap;

            Size textSize = TextRenderer.MeasureText(text, Font, new Size(MaxTextWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int contentHeight = Math.Max(picture is null ? 0 : IconSize, textSize.Height);

            if (picture is not null)
            {
                Controls.Add(new PictureBox
                {
                    Image = picture.ToBitmap(),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Bounds = new Rectangle(Edge, Edge, IconSize, IconSize)
                });
            }

            Controls.Add(new Label
            {
                Text = text,
                AutoSize = false,
                UseMnemonic = false,
                ForeColor = Theme.Text,

                // A line or two sits level with the middle of the icon, as in the system box.
                Bounds = new Rectangle(textLeft, Edge + ((contentHeight - textSize.Height) / 2), textSize.Width + 4, textSize.Height)
            });

            List<(string Text, DialogResult Result)> choices = buttons switch
            {
                MessageBoxButtons.OKCancel => [("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel)],
                MessageBoxButtons.YesNo => [("Yes", DialogResult.Yes), ("No", DialogResult.No)],
                MessageBoxButtons.YesNoCancel => [("Yes", DialogResult.Yes), ("No", DialogResult.No), ("Cancel", DialogResult.Cancel)],
                MessageBoxButtons.RetryCancel => [("Retry", DialogResult.Retry), ("Cancel", DialogResult.Cancel)],
                _ => [("OK", DialogResult.OK)]
            };

            int buttonsWidth = (choices.Count * ButtonWidth) + ((choices.Count - 1) * ButtonGap);
            int width = Math.Max(Math.Max(textLeft + textSize.Width + Edge, buttonsWidth + (2 * Edge)), 300);
            int buttonsTop = Edge + contentHeight + 22;
            ClientSize = new Size(width, buttonsTop + ButtonHeight + 16);

            var created = new List<Button>();
            int left = width - Edge - buttonsWidth;

            for (int i = 0; i < choices.Count; i++)
            {
                var button = new Button
                {
                    Text = choices[i].Text,
                    DialogResult = choices[i].Result,
                    Bounds = new Rectangle(left + (i * (ButtonWidth + ButtonGap)), buttonsTop, ButtonWidth, ButtonHeight),
                    TabIndex = i
                };

                Controls.Add(button);
                created.Add(button);
            }

            int defaultIndex = defaultButton switch
            {
                MessageBoxDefaultButton.Button2 => 1,
                MessageBoxDefaultButton.Button3 => 2,
                _ => 0
            };

            Button preferred = created[Math.Min(defaultIndex, created.Count - 1)];
            AcceptButton = preferred;
            ActiveControl = preferred;

            // Escape answers Cancel or No, as the system box does; with only OK, it is OK.
            CancelButton = created.FirstOrDefault(b => b.DialogResult is DialogResult.Cancel)
                ?? created.FirstOrDefault(b => b.DialogResult is DialogResult.No)
                ?? created[^1];
        }

        private static Icon? IconOf(MessageBoxIcon icon) => icon switch
        {
            MessageBoxIcon.Error => SystemIcons.GetStockIcon(StockIconId.Error, StockIconOptions.Default),
            MessageBoxIcon.Warning => SystemIcons.GetStockIcon(StockIconId.Warning, StockIconOptions.Default),
            MessageBoxIcon.Question => SystemIcons.GetStockIcon(StockIconId.Help, StockIconOptions.Default),
            MessageBoxIcon.Information => SystemIcons.GetStockIcon(StockIconId.Info, StockIconOptions.Default),
            _ => null
        };
    }
}
