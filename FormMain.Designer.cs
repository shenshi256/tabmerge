namespace com.yuanheyuekeji.tabmerge
{
    partial class FormMain
    {
        /// <summary>
        /// 必需的设计器变量。
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// 清理所有正在使用的资源。
        /// </summary>
        /// <param name="disposing">如果应释放托管资源，为 true；否则为 false。</param>
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
        /// 使用代码编辑器修改此方法的内容。
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(FormMain));
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.panel1 = new System.Windows.Forms.Panel();
            this.panel2 = new System.Windows.Forms.Panel();
            this.ChkStartup = new System.Windows.Forms.CheckBox();
            this.ChkLog = new System.Windows.Forms.CheckBox();
            this.ChkMerge = new System.Windows.Forms.CheckBox();
            this.tabControl1.SuspendLayout();
            this.tabPage1.SuspendLayout();
            this.panel1.SuspendLayout();
            this.panel2.SuspendLayout();
            this.SuspendLayout();
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Font = new System.Drawing.Font("微软雅黑", 12F);
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(478, 244);
            this.tabControl1.TabIndex = 0;
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.panel2);
            this.tabPage1.Font = new System.Drawing.Font("微软雅黑", 14F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.tabPage1.Location = new System.Drawing.Point(4, 40);
            this.tabPage1.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Padding = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.tabPage1.Size = new System.Drawing.Size(470, 200);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "设置";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // panel1
            // 
            this.panel1.Controls.Add(this.tabControl1);
            this.panel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panel1.Location = new System.Drawing.Point(0, 0);
            this.panel1.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.panel1.Name = "panel1";
            this.panel1.Size = new System.Drawing.Size(478, 244);
            this.panel1.TabIndex = 1;
            // 
            // panel2
            // 
            this.panel2.Controls.Add(this.ChkMerge);
            this.panel2.Controls.Add(this.ChkLog);
            this.panel2.Controls.Add(this.ChkStartup);
            this.panel2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panel2.Location = new System.Drawing.Point(5, 5);
            this.panel2.Name = "panel2";
            this.panel2.Size = new System.Drawing.Size(460, 190);
            this.panel2.TabIndex = 0;
            // 
            // ChkStartup
            // 
            this.ChkStartup.AutoSize = true;
            this.ChkStartup.Location = new System.Drawing.Point(15, 15);
            this.ChkStartup.Name = "ChkStartup";
            this.ChkStartup.Size = new System.Drawing.Size(153, 40);
            this.ChkStartup.TabIndex = 0;
            this.ChkStartup.Text = "开机启动";
            this.ChkStartup.UseVisualStyleBackColor = true;
            // 
            // ChkLog
            // 
            this.ChkLog.AutoSize = true;
            this.ChkLog.Location = new System.Drawing.Point(15, 75);
            this.ChkLog.Name = "ChkLog";
            this.ChkLog.Size = new System.Drawing.Size(153, 40);
            this.ChkLog.TabIndex = 1;
            this.ChkLog.Text = "开启日志";
            this.ChkLog.UseVisualStyleBackColor = true;
            // 
            // ChkMerge
            // 
            this.ChkMerge.AutoSize = true;
            this.ChkMerge.Checked = true;
            this.ChkMerge.CheckState = System.Windows.Forms.CheckState.Checked;
            this.ChkMerge.Location = new System.Drawing.Point(15, 139);
            this.ChkMerge.Name = "ChkMerge";
            this.ChkMerge.Size = new System.Drawing.Size(153, 40);
            this.ChkMerge.TabIndex = 2;
            this.ChkMerge.Text = "启用合并";
            this.ChkMerge.UseVisualStyleBackColor = true;
            // 
            // FormMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(14F, 31F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(478, 244);
            this.Controls.Add(this.panel1);
            this.Font = new System.Drawing.Font("微软雅黑", 12F);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.MaximizeBox = false;
            this.MaximumSize = new System.Drawing.Size(500, 300);
            this.MinimumSize = new System.Drawing.Size(500, 300);
            this.Name = "FormMain";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "页签合并";
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            this.panel1.ResumeLayout(false);
            this.panel2.ResumeLayout(false);
            this.panel2.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;
        private System.Windows.Forms.Panel panel2;
        private System.Windows.Forms.CheckBox ChkMerge;
        private System.Windows.Forms.CheckBox ChkLog;
        private System.Windows.Forms.CheckBox ChkStartup;
        private System.Windows.Forms.Panel panel1;
    }
}

