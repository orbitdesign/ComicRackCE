using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using cYo.Common.ComponentModel;
using cYo.Common.Drawing;
using cYo.Common.Net;
using cYo.Common.Runtime;
using cYo.Common.Threading;
using cYo.Common.Windows.Forms;
using cYo.Projects.ComicRack.Viewer.Properties;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
    public partial class Splash : LayeredForm
    {
        private volatile int progress;

        private volatile string message;

        private int messageLines = 3;

        private Color progressColor = Color.White;

        private int crashSequence;

        [DefaultValue(false)]
        public bool Fade
        {
            get;
            set;
        }

        [DefaultValue(0)]
        public int Progress
        {
            get
            {
                return progress;
            }
            set
            {
                if (progress != value)
                {
                    progress = value;
                    Invalidate(ProgressBounds);
                    if (!base.InvokeRequired)
                    {
                        Update();
                    }
                }
            }
        }

        [DefaultValue(null)]
        public string Message
        {
            get
            {
                return message;
            }
            set
            {
                if (!(message == value))
                {
                    message = value;
                    Invalidate(MessageBounds);
                    if (!base.InvokeRequired)
                    {
                        Update();
                    }
                }
            }
        }

        [DefaultValue(3)]
        public int MessageLines
        {
            get
            {
                return messageLines;
            }
            set
            {
                if (messageLines != value)
                {
                    messageLines = value;
                    Invalidate(MessageBounds);
                    if (!base.InvokeRequired)
                    {
                        Update();
                    }
                }
            }
        }

        [DefaultValue(typeof(Color), "White")]
        public Color ProgressColor
        {
            get
            {
                return progressColor;
            }
            set
            {
                if (!(progressColor == value))
                {
                    progressColor = value;
                    Invalidate(ProgressBounds);
                }
            }
        }

        public EventWaitHandle Initialized => initialized;

        /// <summary>
        /// Height of the dark band along the bottom of the artwork, which all the text sits in.
        /// </summary>
        protected int BandHeight => FormUtility.ScaleDpiY(34);

        protected Rectangle ProgressBounds
        {
            get
            {
                Rectangle clientRectangle = base.ClientRectangle;
                return new Rectangle(clientRectangle.Left + FormUtility.ScaleDpiX(2), clientRectangle.Bottom - FormUtility.ScaleDpiY(4), clientRectangle.Width - FormUtility.ScaleDpiX(4), FormUtility.ScaleDpiY(2));
            }
        }

        /// <summary>
        /// Startup messages: left half of the band.
        /// </summary>
        protected Rectangle MessageBounds
        {
            get
            {
                Rectangle clientRectangle = base.ClientRectangle;
                return new Rectangle(clientRectangle.Left + FormUtility.ScaleDpiX(10), clientRectangle.Bottom - BandHeight, clientRectangle.Width / 2, BandHeight - FormUtility.ScaleDpiY(6));
            }
        }

        /// <summary>
        /// Copyright and version: right half of the band.
        /// </summary>
        protected Rectangle VersionBounds
        {
            get
            {
                Rectangle clientRectangle = base.ClientRectangle;
                return new Rectangle(clientRectangle.Left + clientRectangle.Width / 2, clientRectangle.Bottom - BandHeight, clientRectangle.Width / 2 - FormUtility.ScaleDpiX(10), BandHeight - FormUtility.ScaleDpiY(6));
            }
        }

        public Splash()
        {
            InitializeComponent();
            base.Surface = Resources.Splash.ScaleDpi();
        }

        protected override void OnLoad(EventArgs e)
        {
            Font = new Font(Font.FontFamily, FormUtility.ScaleDpiY(11), GraphicsUnit.Pixel);
            base.Alpha = 0;
            Show();
            if (Fade)
            {
                ThreadUtility.Animate(0, 250, delegate (float f)
                {
                    base.Alpha = (int)(f * 255f);
                });
            }
            Initialized.Set();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (Fade)
            {
                ThreadUtility.Animate(0, 250, delegate (float f)
                {
                    base.Alpha = 255 - (int)(f * 255f);
                });
            }
            base.OnClosing(e);
        }

        protected override void OnClick(EventArgs e)
        {
            Close();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (crashSequence)
            {
                case 0:
                    if (e.KeyCode == Keys.C)
                    {
                        crashSequence++;
                        return;
                    }
                    break;
                case 1:
                    if (e.KeyCode == Keys.R)
                    {
                        crashSequence++;
                        return;
                    }
                    break;
                case 2:
                    if (e.KeyCode == Keys.A)
                    {
                        crashSequence++;
                        return;
                    }
                    break;
                case 3:
                    if (e.KeyCode == Keys.S)
                    {
                        crashSequence++;
                        return;
                    }
                    break;
                case 4:
                    if (e.KeyCode == Keys.H)
                    {
                        throw new InvalidOperationException("CRASH!");
                    }
                    break;
            }
            Close();
        }

        private static readonly Color MessageColor = Color.FromArgb(204, 204, 204);

        /// <summary>
        /// Draws text with a soft dark outline, so it stays readable over any artwork, light or
        /// dark, without putting a box behind it.
        /// </summary>
        private static void DrawOutlinedString(Graphics g, string text, Font font, Color color, float x, float y, StringFormat format)
        {
            using (Brush shadow = new SolidBrush(Color.FromArgb(150, Color.Black)))
            {
                float d = FormUtility.ScaleDpiX(1);
                g.DrawString(text, font, shadow, x - d, y, format);
                g.DrawString(text, font, shadow, x + d, y, format);
                g.DrawString(text, font, shadow, x, y - d, format);
                g.DrawString(text, font, shadow, x, y + d, format);
            }
            using (Brush brush = new SolidBrush(color))
            {
                g.DrawString(text, font, brush, x, y, format);
            }
        }

        private static void DrawOutlinedString(Graphics g, string text, Font font, Color color, Rectangle bounds, StringFormat format)
        {
            using (Brush shadow = new SolidBrush(Color.FromArgb(150, Color.Black)))
            {
                int d = FormUtility.ScaleDpiX(1);
                foreach (Point offset in new Point[4]
                {
                    new Point(-d, 0),
                    new Point(d, 0),
                    new Point(0, -d),
                    new Point(0, d)
                })
                {
                    Rectangle r = bounds;
                    r.Offset(offset);
                    g.DrawString(text, font, shadow, r, format);
                }
            }
            using (Brush brush = new SolidBrush(color))
            {
                g.DrawString(text, font, brush, bounds, format);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Assembly entryAssembly = Assembly.GetEntryAssembly();
            AssemblyCopyrightAttribute assemblyCopyrightAttribute = Attribute.GetCustomAttribute(entryAssembly, typeof(AssemblyCopyrightAttribute)) as AssemblyCopyrightAttribute;
            string bits = $"{Marshal.SizeOf(typeof(IntPtr)) * 8} bit";
            string buildInfo = GitVersion.GetBuildInfo();
            //Two lines, so it fits the band: the copyright, then the version.
            string version = string.IsNullOrEmpty(buildInfo)
                ? $"V {Application.ProductVersion}{GitVersion.GetCurrentVersionInfo()} {bits}"
                : $"{buildInfo}{GitVersion.GetCurrentVersionInfo()} {bits} - based on Community Edition V {Application.ProductVersion}";
            string str = assemblyCopyrightAttribute.Copyright + "\n" + version;
            using (StringFormat rightFormat = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center
            })
            {
                DrawOutlinedString(e.Graphics, str, Font, Color.White, VersionBounds, rightFormat);
            }
            using (Brush brush = new SolidBrush(progressColor))
            {
                Rectangle progressBounds = ProgressBounds;
                progressBounds.Width = progress * progressBounds.Width / 100;
                e.Graphics.FillRectangle(brush, progressBounds);
            }
            if (!string.IsNullOrEmpty(message))
            {
                using (StringFormat leftFormat = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Far,
                    Trimming = StringTrimming.EllipsisCharacter
                })
                {
                    leftFormat.FormatFlags |= StringFormatFlags.NoWrap;
                    //Newest message at the bottom, older ones stacked above it and fading out.
                    //However many lines the band has room for, up to MessageLines.
                    string[] lines = message.Split('\n');
                    int count = Math.Max(1, Math.Min(messageLines, BandHeight / Math.Max(1, Font.Height)));
                    count = Math.Min(count, lines.Length);
                    Rectangle bounds = MessageBounds;
                    int alpha = 220;
                    int fade = alpha / (count + 1);
                    for (int i = 0; i < count; i++)
                    {
                        DrawOutlinedString(e.Graphics, lines[lines.Length - 1 - i], Font, Color.FromArgb(alpha, MessageColor), bounds, leftFormat);
                        bounds.Height -= Font.Height;
                        alpha -= fade;
                    }
                }
            }
        }
    }
}
