using System.Text.Json;
using Naide.LevelManager.Simulator;

namespace Naide.LevelManager.GatewaySimulator;

internal sealed class SimulatorWindow:Form
{
    private static readonly Color Ink=Color.FromArgb(30,58,68),Teal=Color.FromArgb(22,132,123),Light=Color.FromArgb(243,248,248);
    private GatewayModel model=new(11);
    private LoopbackGateway? server;
    private readonly NumericUpDown port=Number("监听端口",1024,65535,15020),count=Number("模拟罐数量",1,64,11);
    private readonly NumericUpDown level=Number("模拟液位",0,12000,2300,1),temperature=Number("模拟温度",-100,200,24.35m,2),density=Number("模拟密度",1,2000,802,1);
    private readonly Button start=Button("启动模拟",true),stop=Button("停止模拟"),apply=Button("应用数值",true),normal=Button("恢复正常"),offline=Button("模拟离线"),fault=Button("模拟故障");
    private readonly Label state=new(){AutoSize=true,ForeColor=Ink,Text="尚未启动  ·  只在本电脑提供演示数据"},statistics=new(){AutoSize=true,ForeColor=Ink};
    private readonly CheckBox varying=new(){Text="液位缓慢变化",AutoSize=true,Checked=true,ForeColor=Ink};
    private readonly DataGridView grid=new BufferedGrid(){Name="模拟储罐",AccessibleName="模拟储罐",Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,AutoGenerateColumns=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,RowHeadersVisible=false,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    private bool closing,changing;
    private int selected=1;
    private readonly Panel viewport=new BufferedPanel(){Dock=DockStyle.Fill,AutoScroll=true};
    private readonly TableLayoutPanel layout=new BufferedLayout(){Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(24),ColumnCount=1,RowCount=6};
    private readonly Label heading=new(){Text="GT6757X 本机模拟器",AutoSize=true,Margin=new Padding(0,0,0,8)};
    private bool fitting;
    public SimulatorWindow(string[] args)
    {
        AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        Text="GT6757X 本机模拟器  ·  演示工具 V1.0.3";Name="GatewaySimulator";DoubleBuffered=true;Font=new Font("Microsoft YaHei UI",10.5f);BackColor=Light;ForeColor=Ink;ClientSize=new Size(1080,760);MinimumSize=new Size(780,600);StartPosition=FormStartPosition.CenterScreen;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(var i=0;i<6;i++)layout.RowStyles.Add(new RowStyle(i==3?SizeType.Absolute:SizeType.AutoSize,i==3?280:0));
        viewport.Controls.Add(layout);Controls.Add(viewport);
        heading.Font=new Font(Font.FontFamily,19,FontStyle.Bold);heading.Dock=DockStyle.Top;
        var header=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=2,Margin=new Padding(0,0,0,16)};
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        header.Controls.Add(heading,0,0);header.Controls.Add(new Label{Text="模拟展示数据  ·  不连接真实仪表，不占用串口，不改管理系统配置",AutoSize=true,Dock=DockStyle.Top,ForeColor=Teal,Margin=Padding.Empty},0,1);layout.Controls.Add(header,0,0);
        var connect=Flow(12);connect.Controls.Add(Field("端口（本机127.0.0.1）",port,228));connect.Controls.Add(Field("模拟槽位数",count,144));connect.Controls.Add(new Label{Text="Unit ID 固定为 1",AutoSize=true,Margin=new Padding(8,26,12,4)});connect.Controls.Add(start);connect.Controls.Add(stop);layout.Controls.Add(connect,0,1);
        state.Dock=DockStyle.Top;state.Margin=new Padding(0,6,0,10);layout.Controls.Add(state,0,2);
        foreach(var title in new[]{"模拟罐","浮子位置 mm","温度 ℃","密度 kg/m³","状态","仪表模式"})grid.Columns.Add(new DataGridViewTextBoxColumn{HeaderText=title,SortMode=DataGridViewColumnSortMode.NotSortable,FillWeight=title=="仪表模式"?140:100});
        grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None;grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;grid.DefaultCellStyle.Padding=new Padding(4,3,4,3);grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(232,243,241);grid.ColumnHeadersDefaultCellStyle.ForeColor=Ink;grid.DefaultCellStyle.SelectionBackColor=Color.FromArgb(218,239,233);grid.DefaultCellStyle.SelectionForeColor=Ink;grid.GridColor=Color.FromArgb(225,234,235);grid.Margin=Padding.Empty;layout.Controls.Add(grid,0,3);
        var edit=Flow(8);edit.Padding=new Padding(0,12,0,0);edit.Controls.Add(Field("选中罐液位 mm",level,142));edit.Controls.Add(Field("温度 ℃",temperature,128));edit.Controls.Add(Field("密度 kg/m³",density,140));edit.Controls.Add(apply);edit.Controls.Add(normal);edit.Controls.Add(offline);edit.Controls.Add(fault);edit.SetFlowBreak(fault,true);varying.Margin=new Padding(0,8,0,4);edit.Controls.Add(varying);layout.Controls.Add(edit,0,4);
        var footer=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=2,Margin=new Padding(0,4,0,0)};footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        statistics.Text="请求 0 次  ·  模拟写入 0 次";statistics.Dock=DockStyle.Top;statistics.Margin=Padding.Empty;footer.Controls.Add(statistics,0,0);
        footer.Controls.Add(new Label{Text="关闭本工具后模拟罐离线，真实串口通道不受影响。",AutoSize=true,Dock=DockStyle.Top,ForeColor=Ink,Margin=new Padding(0,4,0,0)},0,1);layout.Controls.Add(footer,0,5);
        viewport.SizeChanged+=(_,_)=>FitLayout();
        stop.Enabled=false;EnableEdits(false);UpdateRows();LoadSelection();
        start.Click+=(_,_)=>StartServer();stop.Click+=async(_,_)=>await StopServer();
        apply.Click+=(_,_)=>{model.SetValues(selected,level.Value,temperature.Value,density.Value);UpdateRows();};
        normal.Click+=(_,_)=>Condition(false,0);offline.Click+=(_,_)=>Condition(true,0);fault.Click+=(_,_)=>Condition(false,1);
        varying.CheckedChanged+=(_,_)=>model.Varying=varying.Checked;
        grid.SelectionChanged+=(_,_)=>{if(changing||grid.CurrentRow is null)return;selected=grid.CurrentRow.Index+1;LoadSelection();};
        timer.Tick+=(_,_)=>RefreshValues(server?.Requests??0,server?.Writes??0);timer.Start();
        FormClosing+=async(_,e)=>
        {
            if(closing)return;
            if(server is null){timer.Stop();return;}
            e.Cancel=true;if(MessageBox.Show(this,"关闭后模拟罐会离线，真实液位计通道不受影响。是否关闭？","关闭模拟器",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            closing=true;timer.Stop();await StopServer();Close();
        };
        var verify=Array.IndexOf(args,"--verify-startup");
        Shown+=(_,_)=>{var area=Screen.FromControl(this).WorkingArea;Size=new Size(Math.Min(Width,area.Width),Math.Min(Height,area.Height));CenterToScreen();FitLayout();};
        if(verify>=0&&verify+1<args.Length)Shown+=(_,_)=>{File.WriteAllText(args[verify+1],JsonSerializer.Serialize(new{passed=true,title=Text,rows=grid.Rows.Count,port=port.Value,stopped=server is null,controls=new[]{start.Text,stop.Text,apply.Text,offline.Text,fault.Text}}));BeginInvoke((Action)Close);};
        var verifyLayout=Array.IndexOf(args,"--verify-layout");
        if(verifyLayout>=0&&verifyLayout+1<args.Length)Shown+=(_,_)=>BeginInvoke((Action)(()=>VerifyLayout(args[verifyLayout+1])));
        var verifyRefresh=Array.IndexOf(args,"--verify-refresh");
        if(verifyRefresh>=0&&verifyRefresh+1<args.Length)Shown+=(_,_)=>BeginInvoke((Action)(async()=>await VerifyRefresh(args[verifyRefresh+1])));
    }
    private sealed class BufferedGrid:DataGridView{public BufferedGrid(){DoubleBuffered=true;}}
    private sealed class BufferedPanel:Panel{public BufferedPanel(){DoubleBuffered=true;}}
    private sealed class BufferedLayout:TableLayoutPanel{public BufferedLayout(){DoubleBuffered=true;}}
    private static FlowLayoutPanel Flow(int bottom)=>new(){Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Margin=new Padding(0,0,0,bottom)};
    private static NumericUpDown Number(string name,decimal min,decimal max,decimal value,int places=0)=>new(){Name=name,AccessibleName=name,Minimum=min,Maximum=max,Value=value,DecimalPlaces=places,Increment=places>0?.1m:1,AutoSize=true,Margin=new Padding(0,5,0,0),ThousandsSeparator=false};
    private static Button Button(string text,bool primary=false)=>new(){Text=text,Name=text,AccessibleName=text,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(12,7,12,7),MinimumSize=new Size(94,0),Margin=new Padding(8,22,0,4),FlatStyle=FlatStyle.Flat,BackColor=primary?Teal:Color.White,ForeColor=primary?Color.White:Ink};
    private static Control Field(string title,Control control,int width)
    {
        var p=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,MinimumSize=new Size(width,0),ColumnCount=1,RowCount=2,Margin=new Padding(0,0,12,4),Tag=width};
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));p.RowStyles.Add(new RowStyle(SizeType.AutoSize));p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.Controls.Add(new Label{Text=title,AutoSize=true,Margin=Padding.Empty},0,0);control.MinimumSize=new Size(width,0);control.Width=width;p.Controls.Add(control,0,1);return p;
    }
    private void FitLayout()
    {
        if(fitting||!IsHandleCreated)return;
        fitting=true;
        try
        {
            // DockStyle.Top already follows the scroll panel's available client width.
            // Subtracting the scrollbar again caused a width/scrollbar feedback loop.
            heading.MaximumSize=new Size(Math.Max(1,layout.Width-layout.Padding.Horizontal),0);
            void FitFields(Control parent)
            {
                foreach(Control child in parent.Controls)
                {
                    if(child is TableLayoutPanel field&&field.Tag is int baseWidth)
                    {
                        var available=Math.Max(100,layout.Width-layout.Padding.Horizontal-field.Margin.Horizontal);
                        var width=Math.Min(available,(int)Math.Ceiling(baseWidth*DeviceDpi/96.0*Font.Size/10.5));
                        if(field.MinimumSize.Width!=width)field.MinimumSize=new Size(width,0);
                        foreach(Control part in field.Controls)
                        {
                            if(part is NumericUpDown){if(part.MinimumSize.Width!=width)part.MinimumSize=new Size(width,0);if(part.Width!=width)part.Width=width;}
                            if(part is Label&&part.MaximumSize.Width!=available)part.MaximumSize=new Size(available,0);
                        }
                    }
                    else if(child is not DataGridView)FitFields(child);
                }
            }
            FitFields(layout);
            var rowHeight=TextRenderer.MeasureText("模拟罐 SIM-11",grid.Font).Height+grid.DefaultCellStyle.Padding.Vertical+4;
            grid.RowTemplate.Height=rowHeight;foreach(DataGridViewRow row in grid.Rows)if(row.Height!=rowHeight)row.Height=rowHeight;
            layout.PerformLayout();var rows=layout.GetRowHeights();if(rows.Length!=6)return;
            var other=rows.Where((_,i)=>i!=3).Sum()+layout.Padding.Vertical;
            var height=Math.Max((int)Math.Ceiling(210*DeviceDpi/96.0),viewport.ClientSize.Height-other-4);
            if(Math.Abs(layout.RowStyles[3].Height-height)>1)layout.RowStyles[3].Height=height;
            layout.PerformLayout();
        }
        finally{fitting=false;}
    }
    private void VerifyLayout(string output)
    {
        // Exercise this application's own layout, without changing Windows DPI or other apps.
        var cases=new List<object>();var allErrors=new List<string>();
        foreach(var spec in new[]{(1080,760,10.5f),(960,680,13.125f),(1080,760,15.75f),(800,620,21f)})
        {
            AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(spec.Item1,spec.Item2);Font=new Font("Microsoft YaHei UI",spec.Item3);heading.Font=new Font(Font.FontFamily,spec.Item3*19/10.5f,FontStyle.Bold);
            apply.Text="应用到 SIM-11";state.Text="正在监听 127.0.0.1:15020  ·  Unit ID 1  ·  模拟槽位 1～11";
            statistics.Text="请求 123,456 次  ·  模拟写入 123 次";
            FitLayout();layout.PerformLayout();FitLayout();var errors=new List<string>();
            void Check(Control parent)
            {
                foreach(Control child in parent.Controls)
                {
                    if(child is Label or System.Windows.Forms.Button or CheckBox)
                    {
                        var pref=child.GetPreferredSize(new Size(child.Width,0));
                        if(child.Height+2<pref.Height)errors.Add($"文字高度不足：{child.Text} ({child.Height} < {pref.Height})");
                    }
                    if(child is not DataGridView&&child.Right>parent.ClientSize.Width+2)errors.Add($"超出右边界：{child.GetType().Name}/{child.Text} ({child.Bounds} in {parent.GetType().Name}/{parent.ClientSize}; children={string.Join(';',child.Controls.Cast<Control>().Select(c=>$"{c.GetType().Name}:{c.Text}/{c.Bounds}"))})");
                    if(child.Bottom>parent.ClientSize.Height+2)errors.Add($"超出下边界：{child.Text}");
                    if(child is not DataGridView)Check(child);
                }
            }
            Check(layout);allErrors.AddRange(errors);cases.Add(new{width=ClientSize.Width,height=ClientSize.Height,fontPoints=Font.Size,contentHeight=layout.Height,rowHeights=layout.GetRowHeights(),scroll=viewport.VerticalScroll.Visible,errors});
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new{passed=allErrors.Count==0,title=Text,rows=grid.Rows.Count,stopped=server is null,cases,errors=allErrors}));Close();
    }
    private void RefreshValues(long requests,long writes)
    {
        UpdateRows();var text=$"请求 {requests:N0} 次  ·  模拟写入 {writes:N0} 次";if(statistics.Text!=text)statistics.Text=text;
    }
    private async Task VerifyRefresh(string output)
    {
        timer.Stop();var cases=new List<object>();var errors=new List<string>();
        try
        {
            // This is an in-process UI regression test. It opens no gateway or serial port.
            foreach(var spec in new[]{(1080,760,10.5f),(800,620,15.75f),(1440,960,10.5f)})
            {
                AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(spec.Item1,spec.Item2);Font=new Font("Microsoft YaHei UI",spec.Item3);heading.Font=new Font(Font.FontFamily,spec.Item3*19/10.5f,FontStyle.Bold);
                FitLayout();await Task.Delay(200);FitLayout();await Task.Delay(200);
                viewport.AutoScrollPosition=new Point(0,Math.Min(80,Math.Max(0,layout.Height-viewport.ClientSize.Height)));
                if(grid.Rows.Count>5)grid.FirstDisplayedScrollingRowIndex=3;
                await Task.Delay(100);
                string Geometry()=>JsonSerializer.Serialize(new{content=layout.Size,client=viewport.ClientSize,outerScroll=viewport.VerticalScroll.Visible,position=viewport.AutoScrollPosition,gridFirst=grid.FirstDisplayedScrollingRowIndex,gridScroll=grid.Controls.OfType<VScrollBar>().Any(b=>b.Visible)});
                var baseline=Geometry();var changes=new List<string>();var sizeEvents=0;
                void OnSize(object? sender,EventArgs e)=>sizeEvents++;
                layout.SizeChanged+=OnSize;viewport.ClientSizeChanged+=OnSize;
                for(var tick=0;tick<12;tick++)
                {
                    // Values and counter digit widths change, as during live acquisition.
                    model.SetValues(1,2300+tick/10m,24.35m,802);RefreshValues(998+tick*1000,98+tick);
                    await Task.Delay(250);var current=Geometry();if(current!=baseline)changes.Add(current);
                }
                layout.SizeChanged-=OnSize;viewport.ClientSizeChanged-=OnSize;
                if(changes.Count!=0||sizeEvents!=0)errors.Add($"刷新导致布局或滚动位置变化：{spec.Item1}×{spec.Item2}，{spec.Item3}pt；{changes.Count}次，尺寸事件{sizeEvents}");
                cases.Add(new{width=spec.Item1,height=spec.Item2,fontPoints=spec.Item3,refreshes=12,baseline,changes,sizeEvents});
            }
        }
        catch(Exception e){errors.Add(e.ToString());}
        File.WriteAllText(output,JsonSerializer.Serialize(new{passed=errors.Count==0,title=Text,cases,errors,serialPortsOpened=0,gatewayListeners=0}));Close();
    }
    private void EnableEdits(bool enabled){foreach(var c in new Control[]{apply,normal,offline,fault,level,temperature,density})c.Enabled=enabled;}
    private void StartServer()
    {
        LoopbackGateway? candidate=null;
        try
        {
            var next=new GatewayModel((int)count.Value){Varying=varying.Checked};candidate=new LoopbackGateway(next);candidate.Start((int)port.Value);model=next;server=candidate;
            state.Text=$"正在监听 127.0.0.1:{server.Port}  ·  Unit ID 1  ·  模拟槽位 1～{model.Count}";state.ForeColor=Teal;start.Enabled=false;stop.Enabled=true;port.Enabled=count.Enabled=false;EnableEdits(true);selected=1;UpdateRows();LoadSelection();FitLayout();
        }
        catch(Exception e){if(candidate is not null)candidate.DisposeAsync().AsTask().GetAwaiter().GetResult();MessageBox.Show(this,"启动失败："+e.Message+"\n端口被占用时请换一个端口，并同步修改管理系统模拟通道。不要停止真实采集后台。","模拟器未启动",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }
    private async Task StopServer()
    {
        stop.Enabled=false;var old=server;server=null;if(old is not null)await old.DisposeAsync();state.Text="已停止模拟  ·  真实串口通道不受影响";state.ForeColor=Ink;start.Enabled=true;port.Enabled=count.Enabled=true;EnableEdits(false);FitLayout();
    }
    private void Condition(bool isOffline,ushort code){model.SetCondition(selected,isOffline,code);UpdateRows();}
    private void LoadSelection(){var item=model.Snapshot()[selected-1];level.Value=Math.Clamp(item.PositionMm,level.Minimum,level.Maximum);temperature.Value=item.TemperatureC;density.Value=item.DensityKgM3;apply.Text=$"应用到 SIM-{selected:00}";}
    private void UpdateRows()
    {
        changing=true;
        try{var rows=model.Snapshot();while(grid.Rows.Count<rows.Count)grid.Rows.Add();while(grid.Rows.Count>rows.Count)grid.Rows.RemoveAt(grid.Rows.Count-1);
            foreach(var r in rows){var row=grid.Rows[r.Slot-1];var values=new[]{$"SIM-{r.Slot:00}",r.PositionMm.ToString("F1"),r.TemperatureC.ToString("F2"),r.DensityKgM3.ToString("F1"),r.Condition,Mode(r.Mode)};for(var i=0;i<values.Length;i++)if(!Equals(row.Cells[i].Value,values[i]))row.Cells[i].Value=values[i];var color=r.Condition=="正常"?Ink:Color.FromArgb(175,76,53);if(row.DefaultCellStyle.ForeColor!=color)row.DefaultCellStyle.ForeColor=color;}
        }finally{changing=false;}
    }
    private static string Mode(ushort code)=>code switch{1=>"测量Ⅰ",16=>"测量Ⅱ（界面）",32=>"测量Ⅲ（罐底）",2=>"上升",256=>"下降",4=>"停止",64=>"远程密度",8=>"重复性检查",128=>"液体震荡回避",_=>code.ToString()};
}
