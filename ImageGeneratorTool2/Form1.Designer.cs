using System.Drawing;
using System.Windows.Forms;

namespace ImageGeneratorTool2
{
    public partial class Form1
    {
        /// <summary>
        /// 必需的设计器变量
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// 清理所有正在使用的资源
        /// </summary>
        /// <param name="disposing">如果应释放托管资源，为 true；否则为 false</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows 窗体设计器生成的代码

        /// <summary>
        /// 设计器支持所需的方法 - 不要修改
        /// 使用代码编辑器修改此方法的内容
        /// </summary>
        private void InitializeComponent()
        {
            txtImagePath = new TextBox();
            btnUpload = new Button();
            lblWidth = new Label();
            cboCanvasWidth = new ComboBox();
            btnGenerate = new Button();
            SuspendLayout();
            // 
            // txtImagePath
            // 
            txtImagePath.AllowDrop = true;
            txtImagePath.BackColor = SystemColors.Control;
            txtImagePath.BorderStyle = BorderStyle.FixedSingle;
            txtImagePath.Enabled = false;
            txtImagePath.Location = new Point(20, 23);
            txtImagePath.Name = "txtImagePath";
            txtImagePath.ReadOnly = true;
            txtImagePath.Size = new Size(400, 23);
            txtImagePath.TabIndex = 0;
            // 
            // btnUpload
            // 
            btnUpload.Location = new Point(430, 21);
            btnUpload.Name = "btnUpload";
            btnUpload.Size = new Size(90, 28);
            btnUpload.TabIndex = 1;
            btnUpload.Text = "上传图片";
            btnUpload.UseVisualStyleBackColor = true;
            btnUpload.Click += btnUpload_Click;
            // 
            // lblWidth
            // 
            lblWidth.AutoSize = true;
            lblWidth.Location = new Point(20, 68);
            lblWidth.Name = "lblWidth";
            lblWidth.Size = new Size(80, 17);
            lblWidth.TabIndex = 2;
            lblWidth.Text = "选择画布宽度";
            // 
            // cboCanvasWidth
            // 
            cboCanvasWidth.DropDownStyle = ComboBoxStyle.DropDownList;
            cboCanvasWidth.FormattingEnabled = true;
            cboCanvasWidth.Items.AddRange(new object[] { "4000", "5000", "5500", "6000", "6500", "7000", "8000", "10000" });
            cboCanvasWidth.SelectedIndex = 3;
            cboCanvasWidth.Location = new Point(100, 68);
            cboCanvasWidth.Name = "cboCanvasWidth";
            cboCanvasWidth.Size = new Size(120, 25);
            cboCanvasWidth.TabIndex = 3;
            // 
            // btnGenerate
            // 
            btnGenerate.Location = new Point(20, 113);
            btnGenerate.Name = "btnGenerate";
            btnGenerate.Size = new Size(100, 34);
            btnGenerate.TabIndex = 4;
            btnGenerate.Text = "生成图片";
            btnGenerate.UseVisualStyleBackColor = true;
            btnGenerate.Click += btnGenerate_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(550, 170);
            Controls.Add(btnGenerate);
            Controls.Add(cboCanvasWidth);
            Controls.Add(lblWidth);
            Controls.Add(btnUpload);
            Controls.Add(txtImagePath);
            MaximizeBox = false;
            Name = "Form1";
            Text = "ImageGeneratorTool2 图片生成工具";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        // 控件声明
        private System.Windows.Forms.TextBox txtImagePath;
        private System.Windows.Forms.Button btnUpload;
        private System.Windows.Forms.Label lblWidth;
        private System.Windows.Forms.ComboBox cboCanvasWidth;
        private System.Windows.Forms.Button btnGenerate;
    }
}