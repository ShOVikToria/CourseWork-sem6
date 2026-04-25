using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Linq;

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
        private CheckBox chkEnableThumbnails;

        private RadioButton rbSequential, rbParallel;
        private ComboBox cmbThreads;
        private GroupBox grpSettings;

        private ImageAnalyzer analyzer;
        private List<string> collectionPaths = new List<string>();
        private Rectangle selectionRect;
        private Bitmap referenceCrop = null;

        public Form1()
        {
            InitializeComponent();
            InitializeCustomUI();
            analyzer = new ImageAnalyzer();
        }

        private void InitializeCustomUI()
        {
            this.Text = "Курсова: Пошук об'єктів (Паралельний аналіз)";
            this.Size = new Size(1150, 750);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormClosing += Form1_FormClosing;

            Panel leftPanel = new Panel { Dock = DockStyle.Left, Width = 350, Padding = new Padding(10) };

            // --- БЛОК 1: ЕТАЛОН ---
            btnLoadTemplate = new Button { Text = "1. Завантажити еталон", Dock = DockStyle.Top, Height = 40 };
            btnLoadTemplate.Click += BtnLoadTemplate_Click;

            picTemplate = new PictureBox { Dock = DockStyle.Top, Height = 250, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Cross };
            picTemplate.Paint += PicTemplate_Paint;
            picTemplate.MouseDown += PicTemplate_MouseDown;
            picTemplate.MouseMove += PicTemplate_MouseMove;
            picTemplate.MouseUp += PicTemplate_MouseUp;

            // --- БЛОК 2: КОЛЕКЦІЯ ---
            btnLoadCollection = new Button { Text = "2. Вибрати колекцію", Dock = DockStyle.Top, Height = 40, Margin = new Padding(0, 10, 0, 0) };
            btnLoadCollection.Click += BtnLoadCollection_Click;

            chkEnableThumbnails = new CheckBox { Text = "Показувати мініатюри", Checked = false, Dock = DockStyle.Top, Height = 30 };

            // --- БЛОК 3: НАЛАШТУВАННЯ ПАРАЛЕЛЬНОСТІ ---
            grpSettings = new GroupBox { Text = "Параметри виконання", Dock = DockStyle.Top, Height = 120, Padding = new Padding(10) };

            rbSequential = new RadioButton { Text = "Послідовний режим", Checked = true, Location = new Point(10, 25), AutoSize = true };
            rbParallel = new RadioButton { Text = "Паралельний режим", Location = new Point(10, 50), AutoSize = true };

            Label lblThreads = new Label { Text = "Кількість потоків:", Location = new Point(10, 82), Width = 110 };
            cmbThreads = new ComboBox { Location = new Point(120, 80), Width = 92, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbThreads.Items.AddRange(new object[] { "Авто", "2", "4", "6", "8", "12", "16" });
            cmbThreads.SelectedIndex = 0;
            cmbThreads.Enabled = false;

            rbParallel.CheckedChanged += (s, e) => cmbThreads.Enabled = rbParallel.Checked;

            grpSettings.Controls.Add(rbSequential);
            grpSettings.Controls.Add(rbParallel);
            grpSettings.Controls.Add(lblThreads);
            grpSettings.Controls.Add(cmbThreads);

            // --- БЛОК 4: КЕРУВАННЯ ТА СТАТУС ---
            btnSearch = new Button { Text = "3. ПОШУК", Dock = DockStyle.Top, Height = 50, BackColor = Color.LightGreen, Enabled = false, Font = new Font(this.Font, FontStyle.Bold) };
            btnSearch.Click += BtnSearch_Click;

            btnReset = new Button { Text = "Скинути", Dock = DockStyle.Bottom, Height = 40, BackColor = Color.LightCoral };
            btnReset.Click += BtnReset_Click;

            progressBar = new ProgressBar { Dock = DockStyle.Bottom, Height = 20 };
            lblStatus = new Label { Dock = DockStyle.Bottom, Height = 30, Text = "Очікування...", TextAlign = ContentAlignment.MiddleCenter };

            // Додаємо в зворотному порядку для правильного DockTop
            leftPanel.Controls.Add(btnSearch);
            leftPanel.Controls.Add(grpSettings);
            leftPanel.Controls.Add(chkEnableThumbnails);
            leftPanel.Controls.Add(btnLoadCollection);
            leftPanel.Controls.Add(picTemplate);
            leftPanel.Controls.Add(btnLoadTemplate);
            leftPanel.Controls.Add(lblStatus);
            leftPanel.Controls.Add(progressBar);
            leftPanel.Controls.Add(btnReset);

            // --- ПРАВА ПАНЕЛЬ ---
            SplitContainer splitRight = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            imageListCollection = new ImageList { ImageSize = new Size(80, 80), ColorDepth = ColorDepth.Depth32Bit };
            imageListResults = new ImageList { ImageSize = new Size(100, 100), ColorDepth = ColorDepth.Depth32Bit };

            listCollection = new ListView { Dock = DockStyle.Fill, View = View.List, LargeImageList = imageListCollection };
            listCollection.DoubleClick += List_DoubleClick;

            listResults = new ListView { Dock = DockStyle.Fill, View = View.LargeIcon, LargeImageList = imageListResults, BackColor = Color.Honeydew };
            listResults.DoubleClick += List_DoubleClick;

            splitRight.Panel1.Controls.Add(listCollection);
            splitRight.Panel1.Controls.Add(new Label { Text = "Вся колекція:", Dock = DockStyle.Top, Height = 25, Font = new Font("Arial", 10, FontStyle.Bold) });
            splitRight.Panel2.Controls.Add(listResults);
            splitRight.Panel2.Controls.Add(new Label { Text = "Результати пошуку:", Dock = DockStyle.Top, Height = 25, Font = new Font("Arial", 10, FontStyle.Bold) });

            this.Controls.Add(splitRight);
            this.Controls.Add(leftPanel);
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

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }

        private enum DragMode { None, Move, ResizeBottomRight }
        private DragMode currentDragMode = DragMode.None;
        private Point lastMousePos;

        private void BtnLoadTemplate_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Image Files|*.jpg;*.jpeg;*.png" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    picTemplate.Image = Image.FromFile(ofd.FileName);

                    int margin = 20;
                    // Створюємо початкову рамку
                    selectionRect = new Rectangle(margin, margin, picTemplate.Width - (margin * 2), picTemplate.Height - (margin * 2));

                    // НОВЕ: Автоматично вирізаємо еталон одразу при завантаженні!
                    referenceCrop = CropImage(picTemplate.Image, selectionRect);

                    lblStatus.Text = "Еталон завантажено! Можете змінити рамку або почати пошук.";
                    picTemplate.Invalidate();

                    // НОВЕ: Оновлюємо стан кнопки (вона увімкнеться)
                    CheckReadyToSearch();
                }
            }
        }

        private void PicTemplate_MouseDown(object sender, MouseEventArgs e)
        {
            if (picTemplate.Image == null) return;

            // Створюємо зону 15 пікселів у правому нижньому куті для "зміни розміру"
            Rectangle resizeHandle = new Rectangle(selectionRect.Right - 15, selectionRect.Bottom - 15, 30, 30);

            if (resizeHandle.Contains(e.Location))
            {
                currentDragMode = DragMode.ResizeBottomRight;
            }
            else if (selectionRect.Contains(e.Location))
            {
                currentDragMode = DragMode.Move;
            }
            else
            {
                // Якщо клікнули поза рамкою, малюємо нову з нуля (як було раніше)
                currentDragMode = DragMode.ResizeBottomRight;
                selectionRect = new Rectangle(e.Location, new Size(0, 0));
            }

            lastMousePos = e.Location;
        }

        private void PicTemplate_MouseMove(object sender, MouseEventArgs e)
        {
            if (picTemplate.Image == null) return;

            // Змінюємо курсор, щоб підказати користувачеві, що він може робити
            Rectangle resizeHandle = new Rectangle(selectionRect.Right - 15, selectionRect.Bottom - 15, 30, 30);
            if (resizeHandle.Contains(e.Location))
                picTemplate.Cursor = Cursors.SizeNWSE;
            else if (selectionRect.Contains(e.Location))
                picTemplate.Cursor = Cursors.SizeAll;
            else
                picTemplate.Cursor = Cursors.Cross;

            if (currentDragMode == DragMode.None) return;

            int dx = e.X - lastMousePos.X;
            int dy = e.Y - lastMousePos.Y;

            if (currentDragMode == DragMode.Move)
            {
                // Переміщення рамки з урахуванням меж картинки
                int newX = selectionRect.X + dx;
                int newY = selectionRect.Y + dy;

                // Забороняємо рамці виходити за лівий/верхній край
                newX = Math.Max(0, newX);
                newY = Math.Max(0, newY);

                // Забороняємо рамці виходити за правий/нижній край
                if (newX + selectionRect.Width > picTemplate.ClientSize.Width)
                    newX = picTemplate.ClientSize.Width - selectionRect.Width;
                if (newY + selectionRect.Height > picTemplate.ClientSize.Height)
                    newY = picTemplate.ClientSize.Height - selectionRect.Height;

                selectionRect.X = newX;
                selectionRect.Y = newY;
            }
            else if (currentDragMode == DragMode.ResizeBottomRight)
            {
                // Встановлюємо мінімальний розмір рамки (наприклад, 60 пікселів)
                const int MinSize = 60;

                int newWidth = selectionRect.Width + dx;
                int newHeight = selectionRect.Height + dy;

                // Забороняємо розтягувати рамку за межі картинки вправо і вниз
                if (selectionRect.X + newWidth > picTemplate.ClientSize.Width)
                    newWidth = picTemplate.ClientSize.Width - selectionRect.X;
                if (selectionRect.Y + newHeight > picTemplate.ClientSize.Height)
                    newHeight = picTemplate.ClientSize.Height - selectionRect.Y;

                // Застосовуємо мінімальний ліміт
                selectionRect.Width = Math.Max(MinSize, newWidth);
                selectionRect.Height = Math.Max(MinSize, newHeight);
            }

            lastMousePos = e.Location;
            picTemplate.Invalidate();
        }

        private void PicTemplate_MouseUp(object sender, MouseEventArgs e)
        {
            currentDragMode = DragMode.None;

            // Коли відпустили мишку - фіксуємо еталон
            if (selectionRect.Width > 10 && selectionRect.Height > 10)
            {
                referenceCrop = CropImage(picTemplate.Image, selectionRect);
                lblStatus.Text = "Еталон виділено та зафіксовано!";
                CheckReadyToSearch();
            }
        }

        private void PicTemplate_Paint(object sender, PaintEventArgs e)
        {
            if (selectionRect != null && selectionRect.Width > 0 && selectionRect.Height > 0)
            {
                // Затінюємо фон поза рамкою (для краси, стиль Google Lens)
                Region outerRegion = new Region(picTemplate.ClientRectangle);
                outerRegion.Exclude(selectionRect);
                using (Brush brush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                {
                    e.Graphics.FillRegion(brush, outerRegion);
                }

                // Малюємо саму рамку
                e.Graphics.DrawRectangle(new Pen(Color.LimeGreen, 2), selectionRect);

                // Малюємо "квадратик" для зміни розміру в правому нижньому куті
                e.Graphics.FillRectangle(Brushes.LimeGreen, selectionRect.Right - 5, selectionRect.Bottom - 5, 10, 10);
            }
        }

        private void BtnLoadCollection_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Виберіть головну папку (включно з усіма підпапками)";

                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    collectionPaths.Clear();
                    listCollection.Items.Clear();
                    imageListCollection.Images.Clear();

                    // Показуємо користувачеві, що програма не зависла
                    lblStatus.Text = "Сканування папок...";
                    Application.DoEvents(); // Оновлюємо інтерфейс миттєво

                    // Викликаємо наш новий "розумний" метод
                    collectionPaths = GetImagesRecursively(fbd.SelectedPath);

                    lblStatus.Text = $"Створення мініатюр для {collectionPaths.Count} фото...";
                    Application.DoEvents();

                    bool showThumbs = chkEnableThumbnails.Checked;

                    // Якщо мініатюри вимкнені, перемикаємо ListView у режим списку (це набагато швидше)
                    listCollection.View = showThumbs ? View.LargeIcon : View.List;

                    foreach (string file in collectionPaths)
                    {
                        try
                        {
                            if (showThumbs)
                            {
                                using (Image originalImg = Image.FromFile(file))
                                {
                                    Image thumb = originalImg.GetThumbnailImage(80, 80, () => false, IntPtr.Zero);
                                    imageListCollection.Images.Add(file, thumb);
                                }
                                listCollection.Items.Add(new ListViewItem { ImageKey = file, Text = Path.GetFileName(file), Tag = file });
                            }
                            else
                            {
                                // ПРОСТИЙ РЕЖИМ: жодного зчитування файлу, просто додаємо назву
                                listCollection.Items.Add(new ListViewItem { Text = Path.GetFileName(file), Tag = file });
                            }
                        }
                        catch { /* пропустити биті файли */ }
                    }

                    lblStatus.Text = $"Завантажено {collectionPaths.Count} фото з усіх підпапок.";
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
            if (referenceCrop == null) return;

            btnSearch.Enabled = false;
            listResults.Items.Clear();
            imageListResults.Images.Clear();
            progressBar.Value = 0;

            lblStatus.Text = "Аналіз еталону...";
            await Task.Run(() => analyzer.AnalyzeTemplate(referenceCrop));

            // Визначаємо режим та кількість ядер
            bool useParallel = rbParallel.Checked;
            int threadCount = -1;
            if (useParallel && cmbThreads.Text != "Авто")
            {
                threadCount = int.Parse(cmbThreads.Text);
            }

            lblStatus.Text = useParallel ? $"Паралельний пошук ({cmbThreads.Text} ядер)..." : "Послідовний пошук...";
            Stopwatch sw = Stopwatch.StartNew();

            long lastUiUpdate = 0;

            Action<int, int> progressCallback = (current, total) =>
            {
                long now = Stopwatch.GetTimestamp();
                long elapsed = (now - Interlocked.Read(ref lastUiUpdate)) * 1000 / Stopwatch.Frequency;

                // Оновлюємо UI не частіше ніж раз на 100мс АБО на останньому елементі
                if (elapsed >= 100 || current == total)
                {
                    Interlocked.Exchange(ref lastUiUpdate, now);
                    this.BeginInvoke(new Action(() =>
                    {
                        progressBar.Value = (int)((double)current / total * 100);
                        lblStatus.Text = $"Оброблено: {current} / {total}";
                    }));
                }
            };

            List<string> results;
            if (useParallel)
            {
                results = await Task.Run(() => analyzer.SearchParallel(collectionPaths, progressCallback, threadCount));
            }
            else
            {
                results = await Task.Run(() => analyzer.SearchSequential(collectionPaths, progressCallback));
            }

            sw.Stop();

            // Виведення результатів
            foreach (string file in results)
            {
                try
                {
                    using (Image originalImg = Image.FromFile(file))
                    {
                        Image thumb = originalImg.GetThumbnailImage(100, 100, () => false, IntPtr.Zero);
                        imageListResults.Images.Add(file, thumb);
                    }
                    listResults.Items.Add(new ListViewItem { ImageKey = file, Text = Path.GetFileName(file), Tag = file });
                }
                catch { }
            }

            lblStatus.Text = $"Знайдено: {results.Count}. Режим: {(useParallel ? "Паралельний" : "Послідовний")}. Час: {sw.ElapsedMilliseconds} мс.";
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

        private List<string> GetImagesRecursively(string path)
        {
            List<string> files = new List<string>();
            try
            {
                // 1. Спочатку збираємо всі картинки у поточній папці
                string[] extensions = { ".jpg", ".jpeg", ".png" };
                foreach (string file in Directory.GetFiles(path))
                {
                    if (extensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    {
                        files.Add(file);
                    }
                }

                // 2. Тепер знаходимо всі папки всередині цієї папки...
                // ...і для кожної з них знову викликаємо цей самий метод! (Рекурсія)
                foreach (string directory in Directory.GetDirectories(path))
                {
                    files.AddRange(GetImagesRecursively(directory));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Якщо Windows не пускає в якусь системну підпапку - просто ігноруємо її і йдемо далі
            }
            catch (Exception)
            {
                // Ігноруємо інші можливі помилки читання диска
            }

            return files;
        }
    }
}