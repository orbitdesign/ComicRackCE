using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	/// <summary>
	/// The names the translator met in each series (people, places, ships...) and how it translated them (helper 1.81). Each page
	/// of a book is translated with its series' list, so a name reads the same on every page and in every book of the series. A
	/// translation can be changed, a name added before the series is translated, or one deleted (the next page that has it adds it
	/// again, translated afresh).
	/// </summary>
	public class SeriesNamesDialog : FormEx
	{
		private class LanguageChoice
		{
			public string Label;

			public string Code;

			public override string ToString()
			{
				return Label;
			}
		}

		private readonly TranslatorHelperClient client;

		private List<SeriesNameList> all = new List<SeriesNameList>();

		private readonly ComboBox cboSeries;

		private readonly ComboBox cboLanguage;

		private readonly DataGridView grid;

		private readonly Label lblState;

		private readonly Button btSave;

		private readonly Button btDelete;

		private string shownSeries;

		private string shownLanguage;

		private bool dirty;

		private bool filling;

		public override UIComponent UIComponent => UIComponent.Content;

		public SeriesNamesDialog(string series)
		{
			client = new TranslatorHelperClient(string.IsNullOrWhiteSpace(Program.Settings.TranslatorServerUrl) ? "http://127.0.0.1:8790" : Program.Settings.TranslatorServerUrl);
			Text = "Series Names";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			FormBorderStyle = FormBorderStyle.Sizable;
			StartPosition = FormStartPosition.CenterParent;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(560, 460);
			MinimumSize = new Size(460, 360);
			AnchorStyles topWide = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			AnchorStyles bottomLeft = AnchorStyles.Bottom | AnchorStyles.Left;
			AnchorStyles bottomRight = AnchorStyles.Bottom | AnchorStyles.Right;
			Color gray = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);

			Controls.Add(new Label
			{
				Text = "The names the translator met in each series, and how it translated them. Every page of the series is translated with this list, so a name reads the same in all its books. Change a translation, add a name before translating, or delete one.",
				Location = new Point(12, 8),
				Size = new Size(536, 44),
				Anchor = topWide
			});
			Controls.Add(new Label { Text = "Series:", Location = new Point(12, 63), Size = new Size(56, 18) });
			cboSeries = new ComboBoxEx { Location = new Point(70, 59), Size = new Size(300, 21), DropDownStyle = ComboBoxStyle.DropDownList, Anchor = topWide, Name = "cboSeries" };
			Controls.Add(cboSeries);
			Controls.Add(new Label { Text = "Into:", Location = new Point(382, 63), Size = new Size(36, 18), Anchor = AnchorStyles.Top | AnchorStyles.Right });
			cboLanguage = new ComboBoxEx { Location = new Point(420, 59), Size = new Size(128, 21), DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Top | AnchorStyles.Right, Name = "cboLanguage" };
			cboLanguage.Items.Add(new LanguageChoice { Label = "English", Code = "en" });
			cboLanguage.Items.Add(new LanguageChoice { Label = "Chinese", Code = TranslationButtons.ChineseTarget });
			cboLanguage.SelectedIndex = 0;
			Controls.Add(cboLanguage);

			grid = new DataGridView
			{
				Location = new Point(12, 90),
				Size = new Size(536, 290),
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AllowUserToAddRows = true,
				AllowUserToDeleteRows = true,
				AllowUserToResizeRows = false,
				AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
				RowHeadersWidth = 24,
				SelectionMode = DataGridViewSelectionMode.FullRowSelect,
				Name = "gridNames"
			};
			grid.Columns.Add("name", "Name in the books");
			grid.Columns.Add("translation", "Translation");
			grid.CellValueChanged += delegate { MarkDirty(); };
			grid.UserDeletedRow += delegate { MarkDirty(); };
			Controls.Add(grid);

			lblState = new Label { Location = new Point(12, 388), Size = new Size(536, 30), ForeColor = gray, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "lblState" };
			Controls.Add(lblState);
			btDelete = new Button { Text = "Delete selected", Location = new Point(12, 424), Size = new Size(110, 28), Anchor = bottomLeft, Name = "btDelete" };
			btSave = new Button { Text = "Save", Location = new Point(386, 424), Size = new Size(78, 28), Anchor = bottomRight, Name = "btSave", Enabled = false };
			Button btClose = new Button { Text = "Close", Location = new Point(470, 424), Size = new Size(78, 28), Anchor = bottomRight, Name = "btClose", DialogResult = DialogResult.Cancel };
			Controls.AddRange(new Control[] { btDelete, btSave, btClose });
			CancelButton = btClose;

			btDelete.Click += delegate
			{
				foreach (DataGridViewRow row in grid.SelectedRows.Cast<DataGridViewRow>().Where((DataGridViewRow r) => !r.IsNewRow).ToList())
				{
					grid.Rows.Remove(row);
				}
				MarkDirty();
			};
			btSave.Click += delegate { Save(); };
			cboSeries.SelectedIndexChanged += delegate { ShowNames(); };
			cboLanguage.SelectedIndexChanged += delegate { ShowNames(); };
			FormClosing += delegate (object s, FormClosingEventArgs e)
			{
				if (dirty && MessageBox.Show(this, "Save the changes to the names of \"" + shownSeries + "\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
				{
					e.Cancel = !Save();
				}
			};
			lblState.Text = "Asking the translator...";
			cboSeries.Enabled = cboLanguage.Enabled = grid.Enabled = btDelete.Enabled = false;
			Load += delegate { Fetch(series); };
		}

		/// <summary>Asks the helper for the lists (off the interface thread), then shows the series given, or the first one.</summary>
		private void Fetch(string wanted)
		{
			ThreadPool.QueueUserWorkItem(delegate
			{
				List<SeriesNameList> got = null;
				string problem = null;
				try
				{
					got = client.GetSeriesNames();
				}
				catch (Exception ex)
				{
					problem = TranslationServices.HelperUp ? "The translator could not give the names (it needs helper 1.81 or later): " + ex.Message : "The translator is not running. Start it with the Translation services button.";
				}
				BeginInvokeSafe(delegate
				{
					if (got == null)
					{
						lblState.Text = problem;
						return;
					}
					all = got;
					FillSeries(wanted);
					cboSeries.Enabled = cboLanguage.Enabled = grid.Enabled = btDelete.Enabled = true;
				});
			});
		}

		private void FillSeries(string wanted)
		{
			List<string> names = all.Select((SeriesNameList e) => e.Series).Where((string s) => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			//the selected book's series, even before it has names: names can be given before it is translated
			if (!string.IsNullOrWhiteSpace(wanted) && !names.Contains(wanted, StringComparer.OrdinalIgnoreCase))
			{
				names.Add(wanted);
			}
			names.Sort(StringComparer.OrdinalIgnoreCase);
			filling = true;
			cboSeries.Items.Clear();
			cboSeries.Items.AddRange(names.Cast<object>().ToArray());
			int index = string.IsNullOrWhiteSpace(wanted) ? 0 : names.FindIndex((string s) => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase));
			string language = all.Where((SeriesNameList e) => string.Equals(e.Series, wanted, StringComparison.OrdinalIgnoreCase)).Select((SeriesNameList e) => e.Language).FirstOrDefault();
			if (language != null)
			{
				cboLanguage.SelectedIndex = language.StartsWith("zh") ? 1 : 0;
			}
			filling = false;
			if (names.Count == 0)
			{
				lblState.Text = "No names yet: they are kept as the books of a series are translated (for books whose Series is filled in).";
				return;
			}
			cboSeries.SelectedIndex = Math.Max(0, index);
		}

		private string Code => ((LanguageChoice)cboLanguage.SelectedItem)?.Code ?? "en";

		/// <summary>Shows the names of the series and language chosen (asking first to save the ones changed).</summary>
		private void ShowNames()
		{
			if (filling || cboSeries.SelectedItem == null)
			{
				return;
			}
			if (dirty && shownSeries != null && MessageBox.Show(this, "Save the changes to the names of \"" + shownSeries + "\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
			{
				Save();
			}
			shownSeries = (string)cboSeries.SelectedItem;
			shownLanguage = Code;
			SeriesNameList entry = all.FirstOrDefault((SeriesNameList e) => string.Equals(e.Series, shownSeries, StringComparison.OrdinalIgnoreCase) && e.Language == shownLanguage);
			filling = true;
			grid.Rows.Clear();
			foreach (KeyValuePair<string, string> name in (entry?.Names ?? new Dictionary<string, string>()).OrderBy((KeyValuePair<string, string> n) => n.Key, StringComparer.OrdinalIgnoreCase))
			{
				grid.Rows.Add(name.Key, name.Value);
			}
			filling = false;
			dirty = false;
			btSave.Enabled = false;
			int count = entry?.Names.Count ?? 0;
			lblState.Text = count == 0 ? "No names kept for this series in this language yet. Names typed here are used from the next page translated." : count + (count == 1 ? " name" : " names") + ". The translator uses them for every page of this series translated from now on (pages translated already keep their text).";
		}

		private void MarkDirty()
		{
			if (!filling)
			{
				dirty = true;
				btSave.Enabled = true;
			}
		}

		private bool Save()
		{
			if (shownSeries == null)
			{
				return true;
			}
			Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DataGridViewRow row in grid.Rows.Cast<DataGridViewRow>().Where((DataGridViewRow r) => !r.IsNewRow))
			{
				string name = Convert.ToString(row.Cells[0].Value)?.Trim();
				string translation = Convert.ToString(row.Cells[1].Value)?.Trim();
				if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(translation))
				{
					names[name] = translation;
				}
			}
			try
			{
				all = client.SaveSeriesNames(shownSeries, shownLanguage, names);
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "The names could not be saved: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return false;
			}
			dirty = false;
			btSave.Enabled = false;
			lblState.Text = names.Count + (names.Count == 1 ? " name" : " names") + " saved for \"" + shownSeries + "\".";
			return true;
		}

		private void BeginInvokeSafe(Action action)
		{
			if (IsDisposed || !IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke(action);
			}
			catch (Exception)
			{
			}
		}
	}
}
