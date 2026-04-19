using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;

namespace PictureSearch
{
    public partial class Form1 : Form
    {
        private PictureBox picTemplate;
        private ListView listCollection, listResults;
        private ImageList imageListCollection, imageListResults;
        private Button btnLoadTemplate, btnLoadCollection, btnSearch, btnReset;
        private Label lblStatus;
        private ProgressBar progressBar;

        private ImageAnalyzer analyzer;
        private List<string> collectionPaths = new List<string>();
        private Rectangle selectionRect;
        private Point startPos;
        private bool isDrawing = false;
        private Bitmap referenceCrop = null;

        public Form1()
        {
            InitializeComponent(); // Викликаємо стандартний метод дизайнера
            InitializeCustomUI();  // Викликаємо наш метод
            analyzer = new ImageAnalyzer();
        }

        private void InitializeCustomUI()
        {
            this.Text = "Курсова: Пошук об'єктів (Послідовний режим)";
            this.Size = new Size(1100, 700);
            this.StartPosition = FormStartPosition.CenterScreen;

            Panel leftPanel = new Panel { Dock = DockStyle.Left, Width = 350, Padding = new Padding(10) };

            btnLoadTemplate = new Button { Text = "1. Завантажити еталон", Dock = DockStyle.Top, Height = 40 };
            btnLoadTemplate.Click += BtnLoadTemplate_Click;

            picTemplate = new PictureBox { Dock = DockStyle.Top, Height = 300, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Cross };
            picTemplate.Paint += PicTemplate_Paint;
            picTemplate.MouseDown += PicTemplate_MouseDown;
            picTemplate.MouseMove += PicTemplate_MouseMove;
            picTemplate.MouseUp += PicTemplate_MouseUp;

            btnLoadCollection = new Button { Text = "2. Вибрати колекцію", Dock = DockStyle.Top, Height = 40, Margin = new Padding(0, 10, 0, 0) };
            btnLoadCollection.Click += BtnLoadCollection_Click;

            btnSearch = new Button { Text = "3. ПОШУК", Dock = DockStyle.Top, Height = 50, BackColor = Color.LightGreen, Enabled = false };
            btnSearch.Click += BtnSearch_Click;

            btnReset = new Button { Text = "Скинути", Dock = DockStyle.Bottom, Height = 40, BackColor = Color.LightCoral };
            btnReset.Click += BtnReset_Click;

            progressBar = new ProgressBar { Dock = DockStyle.Bottom, Height = 20 };
            lblStatus = new Label { Dock = DockStyle.Bottom, Height = 30, Text = "Очікування...", TextAlign = ContentAlignment.MiddleCenter };

            leftPanel.Controls.Add(btnSearch);
            leftPanel.Controls.Add(btnLoadCollection);
            leftPanel.Controls.Add(picTemplate);
            leftPanel.Controls.Add(btnLoadTemplate);
            leftPanel.Controls.Add(lblStatus);
            leftPanel.Controls.Add(progressBar);
            leftPanel.Controls.Add(btnReset);

            SplitContainer splitRight = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

            imageListCollection = new ImageList { ImageSize = new Size(80, 80), ColorDepth = ColorDepth.Depth32Bit };
            imageListResults = new ImageList { ImageSize = new Size(100, 100), ColorDepth = ColorDepth.Depth32Bit };

            listCollection = new ListView { Dock = DockStyle.Fill, View = View.LargeIcon, LargeImageList = imageListCollection };
            listCollection.DoubleClick += List_DoubleClick;

            listResults = new ListView { Dock = DockStyle.Fill, View = View.LargeIcon, LargeImageList = imageListResults, BackColor = Color.Honeydew };
            listResults.DoubleClick += List_DoubleClick;

            Panel pnlTop = new Panel { Dock = DockStyle.Fill };
            pnlTop.Controls.Add(listCollection);
            pnlTop.Controls.Add(new Label { Text = "Вся колекція:", Dock = DockStyle.Top, Height = 25, Font = new Font("Arial", 10, FontStyle.Bold) });

            Panel pnlBottom = new Panel { Dock = DockStyle.Fill };
            pnlBottom.Controls.Add(listResults);
            pnlBottom.Controls.Add(new Label { Text = "Результати пошуку:", Dock = DockStyle.Top, Height = 25, Font = new Font("Arial", 10, FontStyle.Bold) });

            splitRight.Panel1.Controls.Add(pnlTop);
            splitRight.Panel2.Controls.Add(pnlBottom);

            this.Controls.Add(splitRight);
            this.Controls.Add(leftPanel);
        }

        private void PicTemplate_MouseDown(object sender, MouseEventArgs e)
        {
            if (picTemplate.Image == null) return;
            isDrawing = true;
            startPos = e.Location;
            selectionRect = new Rectangle();
        }

        private void PicTemplate_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDrawing) return;
            selectionRect = new Rectangle(
                Math.Min(startPos.X, e.X), Math.Min(startPos.Y, e.Y),
                Math.Abs(startPos.X - e.X), Math.Abs(startPos.Y - e.Y));
            picTemplate.Invalidate();
        }

        private void PicTemplate_MouseUp(object sender, MouseEventArgs e)
        {
            isDrawing = false;
            if (selectionRect.Width > 10 && selectionRect.Height > 10)
            {
                referenceCrop = CropImage(picTemplate.Image, selectionRect);
                lblStatus.Text = "Еталон виділено!";
                CheckReadyToSearch();
            }
        }

        private void PicTemplate_Paint(object sender, PaintEventArgs e)
        {
            if (selectionRect != null && selectionRect.Width > 0)
                e.Graphics.DrawRectangle(new Pen(Color.Red, 3), selectionRect);
        }

        private Bitmap CropImage(Image img, Rectangle cropArea)
        {
            float ratioW = (float)img.Width / picTemplate.ClientSize.Width;
            float ratioH = (float)img.Height / picTemplate.ClientSize.Height;
            float ratio = Math.Max(ratioW, ratioH);

            int imgX = (picTemplate.ClientSize.Width - (int)(img.Width / ratio)) / 2;
            int imgY = (picTemplate.ClientSize.Height - (int)(img.Height / ratio)) / 2;

            Rectangle realRect = new Rectangle(
                (int)((cropArea.X - imgX) * ratio),
                (int)((cropArea.Y - imgY) * ratio),
                (int)(cropArea.Width * ratio),
                (int)(cropArea.Height * ratio));

            realRect.Intersect(new Rectangle(0, 0, img.Width, img.Height));

            Bitmap bmpCrop = new Bitmap(realRect.Width, realRect.Height);
            using (Graphics g = Graphics.FromImage(bmpCrop))
            {
                g.DrawImage(img, new Rectangle(0, 0, bmpCrop.Width, bmpCrop.Height), realRect, GraphicsUnit.Pixel);
            }
            return bmpCrop;
        }

        private void BtnLoadTemplate_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Image Files|*.jpg;*.jpeg;*.png" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    picTemplate.Image = Image.FromFile(ofd.FileName);
                    selectionRect = new Rectangle();
                    referenceCrop = null;
                    lblStatus.Text = "Виділіть об'єкт мишкою.";
                }
            }
        }

        private void BtnLoadCollection_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    collectionPaths.Clear();
                    listCollection.Items.Clear();
                    imageListCollection.Images.Clear();

                    string[] files = Directory.GetFiles(fbd.SelectedPath, "*.*");
                    foreach (string file in files)
                    {
                        if (file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        {
                            collectionPaths.Add(file);
                            // Використовуємо using для негайного очищення пам'яті від оригіналу
                            using (Image originalImg = Image.FromFile(file))
                            {
                                Image thumb = originalImg.GetThumbnailImage(80, 80, () => false, IntPtr.Zero);
                                imageListCollection.Images.Add(file, thumb);
                            }
                            listCollection.Items.Add(new ListViewItem { ImageKey = file, Text = Path.GetFileName(file), Tag = file });
                        }
                    }
                    lblStatus.Text = $"Завантажено {collectionPaths.Count} фото.";
                    CheckReadyToSearch();
                }
            }
        }

        private void CheckReadyToSearch()
        {
            btnSearch.Enabled = referenceCrop != null && collectionPaths.Count > 0;
        }

        private void List_DoubleClick(object sender, EventArgs e)
        {
            ListView list = sender as ListView;
            if (list.SelectedItems.Count > 0)
            {
                string filePath = list.SelectedItems[0].Tag.ToString();
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
        }

        private async void BtnSearch_Click(object sender, EventArgs e)
        {
            btnSearch.Enabled = false;
            listResults.Items.Clear();
            imageListResults.Images.Clear();
            progressBar.Value = 0;

            lblStatus.Text = "Аналіз еталону...";
            await Task.Run(() => analyzer.AnalyzeTemplate(referenceCrop));

            lblStatus.Text = $"Шукаю: {analyzer.TargetClass}...";
            Stopwatch sw = Stopwatch.StartNew();

            List<string> results = await Task.Run(() =>
                analyzer.SearchSequential(collectionPaths, (current, total) =>
                {
                    this.Invoke(new Action(() => progressBar.Value = (int)((double)current / total * 100)));
                })
            );

            sw.Stop();

            foreach (string file in results)
            {
                using (Image originalImg = Image.FromFile(file))
                {
                    Image thumb = originalImg.GetThumbnailImage(100, 100, () => false, IntPtr.Zero);
                    imageListResults.Images.Add(file, thumb);
                }
                listResults.Items.Add(new ListViewItem { ImageKey = file, Text = Path.GetFileName(file), Tag = file });
            }

            lblStatus.Text = $"Знайдено: {results.Count}. Час: {sw.ElapsedMilliseconds} мс.";
            btnSearch.Enabled = true;
        }

        private void BtnReset_Click(object sender, EventArgs e)
        {
            picTemplate.Image = null;
            referenceCrop = null;
            selectionRect = new Rectangle();
            collectionPaths.Clear();
            listCollection.Items.Clear();
            listResults.Items.Clear();
            imageListCollection.Images.Clear();
            imageListResults.Images.Clear();
            btnSearch.Enabled = false;
            progressBar.Value = 0;
            lblStatus.Text = "Скинуто.";
        }
    }
}