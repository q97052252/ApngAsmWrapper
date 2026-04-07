using System; // 基础类型（Exception 等）
using System.Drawing; // 图像相关：Image/Bitmap/Graphics
using System.Drawing.Imaging; // 图像格式：ImageFormat.Png
using System.IO; // 文件/路径相关：File/Path
using System.Threading.Tasks; // 异步：Task/async/await
using System.Windows.Forms; // WinForms：Form/Button 等控件
using ApngAsmWrapper; // 引入可复用的 APNG 生成库（NuGet/项目引用后可用）

namespace ImageGeneratorTool2 // 命名空间：用于组织代码，避免类名冲突
{ // 命名空间开始
    /// <summary>
    /// 主窗体（WinForms Form）。
    /// </summary>
    /// <remarks>
    /// 这个类只负责 UI：
    /// - 让用户选择图片、选择画布宽度、选择输出路径
    /// - 显示进度条与提示信息
    /// “生成 APNG 的业务逻辑”放在 <see cref="ApngGenerator"/> 中（更可复用）。
    /// </remarks>
    public partial class Form1 : Form // 主窗体类：继承自 Form（窗口）
    { // 类开始
        // 保存用户选择的图片路径（点击“上传图片”后写入）
        private string _uploadedImagePath = string.Empty;

        // 画布高度固定为 1800（你项目需求里写死的值）
        private const int CanvasHeight = 1800;

        // 把图片缩放到不超过 500（用于让第一帧图片不会太大）
        private const int MaxResizeSize = 500;

        public Form1() // 构造函数：创建窗体对象时会执行
        { // 构造函数开始
            // InitializeComponent 是 WinForms 设计器生成的初始化代码
            // 它会创建按钮/文本框/进度条等控件，并设置布局与事件绑定
            InitializeComponent(); // 初始化所有 UI 控件（设计器生成）
        } // 构造函数结束

        /// <summary>
        /// “上传图片”按钮点击事件：弹出文件选择框，让用户选择一张图片。
        /// </summary>
        /// <param name="sender">事件发送者（按钮）。</param>
        /// <param name="e">事件参数。</param>
        private void btnUpload_Click(object sender, EventArgs e) // “上传图片”按钮点击事件处理函数
        { // 方法开始
            try // 用 try/catch 捕获异常，避免程序崩溃
            { // try 开始
                // 打开文件选择对话框
                using OpenFileDialog openFileDialog = new OpenFileDialog // 创建文件选择对话框（using：自动释放资源）
                { // 初始化对象属性开始
                    Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif", // 只显示这些图片格式
                    Title = "选择图片" // 对话框标题
                }; // 初始化对象属性结束

                // 用户点了“确定”后，读取文件路径
                if (openFileDialog.ShowDialog() == DialogResult.OK) // 显示对话框；用户点“确定”才继续
                { // if 开始
                    _uploadedImagePath = openFileDialog.FileName; // 记录用户选择的文件路径
                    txtImagePath.Text = _uploadedImagePath; // 把路径显示到界面文本框里
                } // if 结束
            } // try 结束
            catch (Exception ex) // 捕获任何异常
            { // catch 开始
                // 简单提示错误（例如没有权限、对话框异常等）
                MessageBox.Show($"错误：{ex.Message}"); // 弹窗提示错误原因
            } // catch 结束
        } // 方法结束

        /// <summary>
        /// “生成图片”按钮点击事件：生成两帧，然后调用 apngasm 合成 APNG。
        /// </summary>
        /// <param name="sender">事件发送者（按钮）。</param>
        /// <param name="e">事件参数。</param>
        private async void btnGenerate_Click(object sender, EventArgs e) // “生成图片”按钮点击事件（async：允许 await）
        { // 方法开始
            // 进入“忙碌状态”：禁用按钮、显示等待光标
            SetBusyUiState(isBusy: true); // 进入忙碌状态（禁用按钮/显示等待光标）
            try // 捕获生成过程的异常
            { // try 开始
                // 1) 基础校验：必须先上传图片
                if (string.IsNullOrEmpty(_uploadedImagePath) || !File.Exists(_uploadedImagePath)) // 没有选择文件或文件不存在
                { // if 开始
                    MessageBox.Show("请先上传图片！"); // 提示用户先上传图片
                    return; // 结束本次点击处理
                } // if 结束

                // 2) 必须选择画布宽度（ComboBox）
                if (cboCanvasWidth.SelectedItem is null) // 没有选择画布宽度
                { // if 开始
                    MessageBox.Show("请先选择画布宽度！"); // 提示用户选择宽度
                    return; // 结束本次点击处理
                } // if 结束

                // 3) 读取画布宽度（下拉框里是数字字符串）
                int canvasWidth = int.Parse(cboCanvasWidth.SelectedItem.ToString()!); // 把下拉框选项（字符串）转成 int

                // 4) 从磁盘加载原始图片（Bitmap 实现了 IDisposable，所以要用 using 释放）
                using (Bitmap originalImage = (Bitmap)Image.FromFile(_uploadedImagePath)) // 从磁盘读取图片（会占用文件句柄）

                // 5) 创建透明画布（ARGB）
                using (Bitmap canvas = new Bitmap(canvasWidth, CanvasHeight, PixelFormat.Format32bppArgb)) // 创建透明画布（ARGB）

                // 6) 在画布上绘制（Graphics 也要释放）
                using (Graphics g = Graphics.FromImage(canvas)) // 从画布创建绘图对象
                { // using(Graphics) 代码块开始
                    // 清空画布为透明
                    g.Clear(Color.Transparent);

                    // 设置高质量绘制（更清晰，但会更慢）
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                    // 绘制第一帧
                    // 计算缩放后的尺寸，让图片最大边不超过 MaxResizeSize
                    Size resizeSize = GetResizeSize(originalImage.Width, originalImage.Height, MaxResizeSize);

                    // 计算居中位置
                    int posX = (canvasWidth - resizeSize.Width) / 2;
                    int posY = (CanvasHeight - resizeSize.Height) / 2;

                    // 把原图缩放后画到画布上
                    g.DrawImage(originalImage, posX, posY, resizeSize.Width, resizeSize.Height);

                    // 复制当前画布作为第一帧（Clone 得到新的 Image，需要 using 释放）
                    using (Image firstFrame = (Image)canvas.Clone()) // 把当前画布复制成第一帧
                    { // 第一帧代码块开始
                        // 绘制第二帧
                        // 清空画布，再画文字
                        g.Clear(Color.Transparent);
                        string text = "这是第二帧内容";
                        using (Font font = new Font("微软雅黑", 48, FontStyle.Bold)) // 创建字体对象
                        { // using(Font) 代码块开始
                            // MeasureString：测量文字尺寸，用于居中
                            SizeF textSize = g.MeasureString(text, font);
                            int tx = (canvasWidth - (int)textSize.Width) / 2;
                            int ty = (CanvasHeight - (int)textSize.Height) / 2;
                            g.DrawString(text, font, Brushes.White, tx, ty);
                        }

                        // 复制当前画布作为第二帧
                        using (Image secondFrame = (Image)canvas.Clone()) // 把当前画布复制成第二帧
                        { // 第二帧代码块开始
                            // 让用户选择保存 APNG 的路径
                            using SaveFileDialog saveFileDialog = new SaveFileDialog // 保存文件对话框
                            { // 初始化对象属性开始
                                Filter = "APNG|*.png", // 只保存 png（apng 也是 png）
                                Title = "保存 APNG", // 标题
                                FileName = "output.png" // 默认文件名
                            }; // 初始化对象属性结束

                            // 用户取消保存就直接返回
                            if (saveFileDialog.ShowDialog() != DialogResult.OK) // 用户取消保存
                                return; // 直接返回

                            // 把生成 APNG 需要的参数打包成 Request
                            using Image f1 = (Image)firstFrame.Clone();
                            using Image f2 = (Image)secondFrame.Clone();

                            var options = new ApngGenerator.Options
                            {
                                SkipFirstFrame = true,
                                LoopCount = 1,
                                CompressionMode = ApngGenerator.Compression.SevenZip,
                            };

                            ApngGenerator.Request req = new ApngGenerator.Builder(saveFileDialog.FileName)
                                .WithOptions(options)
                                // 这里传入 3 帧：frame00/01/02
                                // frame00: 静态兜底帧（会被 -f 跳过，不参与动画）
                                .AddFrame(f1) // 不写 delay，使用默认 delay 或忽略
                                // 动画帧 1：secondFrame，3 秒
                                .AddFrame(f2, delayNum: 3, delayDen: 1)
                                // 动画帧 2：回到 firstFrame，1 秒
                                .AddFrame(f1, delayNum: 1, delayDen: 1)
                                .Build();

                            ApngGenerator.Result result = await ApngGenerator.GenerateAsync(req);
                            if (!result.Success)
                            {
                                MessageBox.Show(result.ErrorMessage ?? "生成失败", "生成失败");
                                return;
                            }

                            MessageBox.Show("已生成 APNG。\r\n注意：Windows 自带图片查看器可能不播放 APNG，请用 Chrome/Edge 查看。", "生成结果");
                        } // 第二帧代码块结束
                    } // 第一帧代码块结束
                } // using(Graphics) 代码块结束
            } // try 结束
            catch (Exception ex) // 捕获所有异常（例如 IO/GDI+）
            { // catch 开始
                // 未预期异常（例如 GDI+ 保存失败、路径非法等），显示完整堆栈方便排查
                MessageBox.Show(ex.ToString(), "生成失败"); // 显示完整异常（含堆栈）
            } // catch 结束
            finally // 无论成功还是失败都执行
            { // finally 开始
                // 退出忙碌状态：恢复按钮/光标
                SetBusyUiState(isBusy: false); // 恢复 UI（启用按钮/恢复光标）
            } // finally 结束
        } // 方法结束

        /// <summary>
        /// 设置界面“忙碌/空闲”状态（统一管理按钮、光标）。
        /// </summary>
        /// <param name="isBusy">true 表示正在生成；false 表示空闲。</param>
        private void SetBusyUiState(bool isBusy) // 统一设置“忙碌/空闲”UI 状态
        { // 方法开始
            // 生成中禁用按钮，防止重复点击
            btnGenerate.Enabled = !isBusy;

            // 切换鼠标光标
            Cursor = isBusy ? Cursors.WaitCursor : Cursors.Default;
        } // 方法结束

        /// <summary>
        /// 计算缩放后的尺寸：把图片等比缩放到最大边不超过 max。
        /// </summary>
        /// <param name="w">原图宽。</param>
        /// <param name="h">原图高。</param>
        /// <param name="max">最大边限制。</param>
        /// <returns>缩放后的 Size。</returns>
        private Size GetResizeSize(int w, int h, int max) // 计算等比缩放后的尺寸
        { // 方法开始
            // 如果原图本来就不超过 max，就不需要缩放
            if (w <= max && h <= max)
                return new Size(w, h);

            // 计算缩放比例：让最大边缩到 max
            double ratio = (double)max / Math.Max(w, h);

            // 返回缩放后的宽高
            return new Size((int)(w * ratio), (int)(h * ratio));
        } // 方法结束

    } // 类结束
} // 命名空间结束