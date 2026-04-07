using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using ApngAsmWrapper;

namespace ImageGeneratorTool2
{
    public partial class Form1 : Form
    {
        private string _uploadedImagePath = string.Empty;

        private const int CanvasHeight = 1800;

        private const int MaxResizeSize = 500;

        public Form1()
        {
            InitializeComponent();
        }

        private void btnUpload_Click(object sender, EventArgs e)
        {
            try
            {
                using OpenFileDialog openFileDialog = new OpenFileDialog
                {
                    Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif",
                    Title = "选择图片"
                };

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    _uploadedImagePath = openFileDialog.FileName;
                    txtImagePath.Text = _uploadedImagePath;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"错误：{ex.Message}");
            }
        }

        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            SetBusyUiState(isBusy: true);
            try
            {
                if (string.IsNullOrEmpty(_uploadedImagePath) || !File.Exists(_uploadedImagePath))
                {
                    MessageBox.Show("请先上传图片！");
                    return;
                }

                if (cboCanvasWidth.SelectedItem is null)
                {
                    MessageBox.Show("请先选择画布宽度！");
                    return;
                }

                int canvasWidth = int.Parse(cboCanvasWidth.SelectedItem.ToString()!);

                using (Bitmap originalImage = (Bitmap)Image.FromFile(_uploadedImagePath))
                using (Bitmap canvas = new Bitmap(canvasWidth, CanvasHeight, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);

                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                    Size resizeSize = GetResizeSize(originalImage.Width, originalImage.Height, MaxResizeSize);

                    int posX = (canvasWidth - resizeSize.Width) / 2;
                    int posY = (CanvasHeight - resizeSize.Height) / 2;

                    g.DrawImage(originalImage, posX, posY, resizeSize.Width, resizeSize.Height);

                    using (Image firstFrame = (Image)canvas.Clone())
                    {
                        g.Clear(Color.Transparent);
                        string text = "这是第二帧内容";
                        using (Font font = new Font("微软雅黑", 48, FontStyle.Bold))
                        {
                            SizeF textSize = g.MeasureString(text, font);
                            int tx = (canvasWidth - (int)textSize.Width) / 2;
                            int ty = (CanvasHeight - (int)textSize.Height) / 2;
                            g.DrawString(text, font, Brushes.White, tx, ty);
                        }

                        using (Image secondFrame = (Image)canvas.Clone())
                        {
                            using SaveFileDialog saveFileDialog = new SaveFileDialog
                            {
                                Filter = "APNG|*.png",
                                Title = "保存 APNG",
                                FileName = $"PNG{DateTime.Now:yyyyMMddHHmmss}.png"
                            };

                            if (saveFileDialog.ShowDialog() != DialogResult.OK)
                                return;

                            using Image f1 = (Image)firstFrame.Clone();
                            using Image f2 = (Image)secondFrame.Clone();

                            var options = new ApngGenerator.Options
                            {
                                SkipFirstFrame = true,
                                LoopCount = 1
                            };

                            ApngGenerator.Request req = new ApngGenerator.Builder(saveFileDialog.FileName)
                                .WithOptions(options)
                                .AddFrame(f1, delayMs: 10)
                                .AddFrame(f2, delayMs: 1000 * 200)
                                .AddFrame(f1, delayMs: 10)
                                .Build();

                            ApngGenerator.Result result = await ApngGenerator.GenerateAsync(req);
                            if (!result.Success)
                            {
                                MessageBox.Show(result.ErrorMessage ?? "生成失败", "生成失败");
                                return;
                            }

                            MessageBox.Show("已生成 APNG。\r\n注意：Windows 自带图片查看器可能不播放 APNG，请用 Chrome/Edge 查看。", "生成结果");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "生成失败");
            }
            finally
            {
                SetBusyUiState(isBusy: false);
            }
        }

        private void SetBusyUiState(bool isBusy)
        {
            btnGenerate.Enabled = !isBusy;
            Cursor = isBusy ? Cursors.WaitCursor : Cursors.Default;
        }

        private Size GetResizeSize(int w, int h, int max)
        {
            if (w <= max && h <= max)
                return new Size(w, h);

            double ratio = (double)max / Math.Max(w, h);
            return new Size((int)(w * ratio), (int)(h * ratio));
        }
    }
}
