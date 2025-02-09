﻿using Jvedio.Entity.CommonSQL;
using SuperUtils.Framework.MarkDown;
using SuperUtils.Framework.ORM.Wrapper;
using System;
using System.Windows;

namespace Jvedio
{
    /// <summary>
    /// Settings.xaml 的交互逻辑
    /// </summary>
    public partial class Dialog_Notice : SuperControls.Style.BaseDialog
    {
        public string Message { get; set; }

        public Dialog_Notice(Window owner, bool showbutton, string message) : base(owner, showbutton)
        {
            InitializeComponent();
            Message = message;
            this.ContentRendered += RenderContent;
        }

        public void RenderContent(object sender, EventArgs e)
        {
            richTextBox.Document = MarkDown.parse(Message);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            string configName = "Notice";

            // 获取本地的公告
            string notices = string.Empty;
            SelectWrapper<AppConfig> wrapper = new SelectWrapper<AppConfig>();
            wrapper.Eq("ConfigName", configName);
            AppConfig appConfig = MapperManager.appConfigMapper.SelectOne(wrapper);
            if (appConfig != null && !string.IsNullOrEmpty(appConfig.ConfigValue))
                notices = appConfig.ConfigValue.Replace(SuperUtils.Values.ConstValues.Separator, '\n');
            this.Message = notices;
            RenderContent(sender, e);
        }
    }
}