using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

// Native per-pixel-alpha window: no browser frame, background or taskbar button.
class CrocDesktop : Form {
    [StructLayout(LayoutKind.Sequential)] struct PointNative { public int X,Y; public PointNative(int x,int y){X=x;Y=y;} }
    [StructLayout(LayoutKind.Sequential)] struct SizeNative { public int X,Y; public SizeNative(int x,int y){X=x;Y=y;} }
    [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend { public byte Op,Flags,Alpha,Format; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr dst,ref PointNative pos,ref SizeNative size,IntPtr src,ref PointNative source,int color,ref Blend blend,int flags);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr w,IntPtr l);
    readonly string root, runtime;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly Timer timer=new Timer();
    readonly ContextMenuStrip contextMenu=new ContextMenuStrip();
    Image rest,work,bottle,poop;
    Bitmap frame;
    NotifyIcon tray;
    Dictionary<string,object> state=new Dictionary<string,object>();
    List<Particle> particles=new List<Particle>();
    string lastRun="", status="正在连接本地日志";
    double scale=1, emission=0, demoUntil=0, demoSeconds=2;
    int bottles=0, lanes=0;
    bool working=false,emitting=false,dragging=false,loaded=false,visibleScene=true;
    DateTime previous=DateTime.UtcNow, lastRead=DateTime.MinValue;
    class Particle { public float X,Y,Speed; public int Row; }
    float RowY(int row){return 80+row*22;}
    float FrontEdge(float y){
        var bitmap=(Bitmap)work;
        int py=Math.Max(0,Math.Min(bitmap.Height-1,(int)((y-35)*bitmap.Height/164)));
        for(int x=0;x<bitmap.Width;x++)if(bitmap.GetPixel(x,py).A>128)return 125+x*164f/bitmap.Width;
        return 155;
    }
    static readonly int[] LaneOrder={3,2,4,1,0}; // Butt first, then spread above/below.
    float RowStart(int row){
        // Follow the actual back/tail contour, instead of a fixed empty column.
        var bitmap=(Bitmap)work;
        int py=Math.Max(0,Math.Min(bitmap.Height-1,(int)((RowY(row)-35)*bitmap.Height/164)));
        for(int x=bitmap.Width-1;x>=0;x--)if(bitmap.GetPixel(x,py).A>128)return 125+x*164f/bitmap.Width-4;
        return 250;
    }
    static double Now(){return (DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds;}
    string Value(string key,string fallback=""){object v;return state.TryGetValue(key,out v)&&v!=null?v.ToString():fallback;}
    double Number(string key,double fallback=0){double n;return double.TryParse(Value(key),out n)?n:fallback;}
    bool Flag(string key){return Value(key)=="True";}
    protected override CreateParams CreateParams { get {var p=base.CreateParams;p.ExStyle|=0x80000|0x80;return p;} }
    protected override bool ShowWithoutActivation {get{return true;}}
    public CrocDesktop(string project,string data,bool demo,bool smoke){
        root=project;runtime=data;Directory.CreateDirectory(runtime);
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;
        Text="Croc Token 桌面鳄鱼";
        rest=Image.FromFile(Path.Combine(root,"web/assets/croc-rest.png"));work=Image.FromFile(Path.Combine(root,"web/assets/croc-work.png"));
        bottle=Image.FromFile(Path.Combine(root,"web/assets/bottle.png"));poop=Image.FromFile(Path.Combine(root,"web/assets/poop.png"));
        var area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-450,area.Bottom-230);
        try{var saved=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(runtime,"layout.json")));scale=Convert.ToDouble(saved["scale"]);Location=new Point(Convert.ToInt32(saved["x"]),Convert.ToInt32(saved["y"]));}catch{}
        scale=Math.Max(.7,Math.Min(2,scale));ClientSize=new Size((int)(420*scale),(int)(220*scale));
        bool onScreen=false;foreach(var screen in Screen.AllScreens)if(screen.WorkingArea.IntersectsWith(Bounds))onScreen=true;
        if(!onScreen)Location=new Point(area.Right-Width-20,area.Bottom-Height-20);
        tray=new NotifyIcon();tray.Icon=SystemIcons.Application;tray.Text="桌面鳄鱼 · 右键设置";tray.Visible=true;
        tray.MouseClick+=(s,e)=>{if(e.Button==MouseButtons.Right)Popup().Show(Cursor.Position);else if(e.Button==MouseButtons.Left){visibleScene=true;Show();Render();}};
        MouseDown+=(s,e)=>{if(e.Button==MouseButtons.Left){dragging=true;ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);dragging=false;Save();}else if(e.Button==MouseButtons.Right)Popup().Show(Cursor.Position);};
        MouseWheel+=(s,e)=>ResizeScene(scale+(e.Delta>0?.1:-.1));
        timer.Interval=25;timer.Tick+=(s,e)=>Tick();
        Shown+=(s,e)=>{loaded=true;if(demo)Demo(2);timer.Start();Render();};
        if(smoke){var check=new Timer();check.Interval=1000;check.Tick+=(s,e)=>{
            check.Stop();timer.Stop();
            try{
                if(!IsWindowVisible(Handle)||frame==null||!working||bottles!=5||lanes!=5)throw new Exception("Working window state invalid");
                if(frame.GetPixel(0,0).A!=0)throw new Exception("Background is not transparent");
                frame.Save(Path.Combine(runtime,"working-preview.png"),ImageFormat.Png);
                working=false;emitting=false;Render();
                if(working)throw new Exception("End did not restore resting pose");
                frame.Save(Path.Combine(runtime,"resting-preview.png"),ImageFormat.Png);
                state["model"]="smoke-model";state["cache_percent"]=80;
                for(int i=0;i<8;i++){var testMenu=Popup();testMenu.Show(PointToScreen(new Point(30,30)));Application.DoEvents();testMenu.Close();Application.DoEvents();if(testMenu.IsDisposed)throw new Exception("Context menu disposed while handling messages");}
                if(!contextMenu.Items[2].Text.Contains("smoke-model")||!contextMenu.Items[3].Text.Contains("80"))throw new Exception("Model/cache menu fields missing");
                File.WriteAllText(Path.Combine(runtime,"smoke-result.json"),json.Serialize(new{visible=true,transparent=true,working=true,restored=true,menuCycles=8,modelAndCache=true,width=Width,height=Height}));
            }catch(Exception error){File.WriteAllText(Path.Combine(runtime,"smoke-result.json"),json.Serialize(new{error=error.ToString()}));}
            Close();check.Dispose();};Shown+=(s,e)=>check.Start();}
        FormClosed+=(s,e)=>{timer.Stop();Save();tray.Dispose();contextMenu.Dispose();rest.Dispose();work.Dispose();bottle.Dispose();poop.Dispose();if(frame!=null)frame.Dispose();};
    }
    void Save(){try{File.WriteAllText(Path.Combine(runtime,"layout.json"),json.Serialize(new{ x=Left,y=Top,scale=scale}));}catch{}}
    void ResizeScene(double value){scale=Math.Max(.7,Math.Min(2,value));ClientSize=new Size((int)(420*scale),(int)(220*scale));Save();Render();}
    void Demo(double seconds){particles.Clear();demoSeconds=seconds;demoUntil=Now()+12;working=true;emitting=true;bottles=5;lanes=5;emission=40;status="模拟演示 · 12 秒后恢复真实数据";}
    ContextMenuStrip Popup(){
        // WinForms may still access the dropdown after Closed; keep it alive
        // until the owner window closes instead of disposing in that event.
        var menu=contextMenu;if(menu.Visible)return menu;
        while(menu.Items.Count>0){var oldItem=menu.Items[0];menu.Items.RemoveAt(0);oldItem.Dispose();}
        menu.Items.Add(status).Enabled=false;
        menu.Items.Add(Value("following","最新活动")+(Value("selected").Length>0?" · "+Value("selected").Substring(Math.Max(0,Value("selected").Length-8)):"")).Enabled=false;
        menu.Items.Add("模型："+Value("model","暂无记录")).Enabled=false;
        menu.Items.Add("最近响应缓存命中："+(Value("cache_percent").Length==0?"暂无计数":Number("cache_percent").ToString("0.#")+"%" )).Enabled=false;
        menu.Items.Add("最近响应输入 / 输出："+Value("last_input","—")+" / "+Value("last_output","—")+" Token").Enabled=false;
        menu.Items.Add("其中缓存输入："+Value("cached_input","—")+" Token").Enabled=false;
        menu.Items.Add("本次投喂：文本约 "+Value("new_input_estimate","未知")+" Token（粗估）"+(Flag("attachments_unknown")?"；附件计量未知":"")).Enabled=false;
        menu.Items.Add(new ToolStripSeparator());
        var design=new ToolStripMenuItem("分级设计与署名");
        design.DropDownItems.Add("奶瓶 1–5 个：本次消息文本量（粗估）").Enabled=false;
        design.DropDownItems.Add("投喂分界：100 / 500 / 2千 / 8千 Token").Enabled=false;
        design.DropDownItems.Add("排除历史上下文；附件仅提示存在，不伪造计数").Enabled=false;
        design.DropDownItems.Add("便便 1–5 行：本轮累计输出量").Enabled=false;
        design.DropDownItems.Add("输出分界：500 / 2千 / 5千 / 1.5万 Token").Enabled=false;
        design.DropDownItems.Add("慢悠悠 <30；匀速 30–79.99；飞快 ≥80 tok/s").Enabled=false;
        design.DropDownItems.Add("穿过输出区：4 秒 / 2 秒 / 0.8 秒").Enabled=false;
        design.DropDownItems.Add("速率与缓存来自最近完成响应，非瞬时值").Enabled=false;
        design.DropDownItems.Add("交互设计：Hziibepacient").Enabled=false;
        design.DropDownItems.Add("数据基础：drkang7 / Codex Token HUD · MIT").Enabled=false;
        menu.Items.Add(design);
        var threads=new ToolStripMenuItem("选择观察的对话");
        threads.DropDownItems.Add("自动跟随最新活动",null,(s,e)=>Choose(""));
        object rows;if(state.TryGetValue("threads",out rows)){foreach(var row in (IEnumerable)rows){var r=row as Dictionary<string,object>;if(r==null)continue;string id=r["id"].ToString(),title=r["title"].ToString();var item=new ToolStripMenuItem(title.Length>45?title.Substring(0,45)+"…":title);item.ToolTipText=id;item.Checked=id==Value("selected");item.Click+=(s,e)=>Choose(id);threads.DropDownItems.Add(item);}}
        menu.Items.Add(threads);
        var sizes=new ToolStripMenuItem("整体大小（也可滚轮调整）");
        foreach(double size in new[]{.7,1,1.3,1.6,2}){double chosen=size;sizes.DropDownItems.Add((size*100)+"%",null,(s,e)=>ResizeScene(chosen));}menu.Items.Add(sizes);
        var demos=new ToolStripMenuItem("试播动画");
        demos.DropDownItems.Add("慢悠悠 · 4 秒穿过",null,(s,e)=>Demo(4));demos.DropDownItems.Add("匀速 · 2 秒穿过",null,(s,e)=>Demo(2));demos.DropDownItems.Add("飞快 · 0.8 秒穿过",null,(s,e)=>Demo(.8));menu.Items.Add(demos);
        menu.Items.Add("模拟结束 / 恢复真实数据",null,(s,e)=>{demoUntil=0;working=false;emitting=false;lastRead=DateTime.MinValue;Render();});
        var top=new ToolStripMenuItem("始终置顶");top.Checked=TopMost;top.Click+=(s,e)=>{TopMost=!TopMost;};menu.Items.Add(top);
        menu.Items.Add(visibleScene?"隐藏（托盘可恢复）":"显示",null,(s,e)=>{visibleScene=!visibleScene;if(visibleScene)Show();else Hide();});
        menu.Items.Add("退出",null,(s,e)=>BeginInvoke(new Action(Close)));return menu;
    }
    void Choose(string id){File.WriteAllText(Path.Combine(runtime,"selection.json"),json.Serialize(new{thread_id=id}));particles.Clear();bottles=0;lanes=0;demoUntil=0;lastRun="";}
    void Tick(){
        var now=DateTime.UtcNow;float dt=(float)Math.Min(.05,(now-previous).TotalSeconds);previous=now;
        if((now-lastRead).TotalSeconds>.35){lastRead=now;try{state=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(runtime,"scene.json")));}catch{}}
        double seconds=demoSeconds;
        if(demoUntil>Now()) {working=true;emitting=true;bottles=5;lanes=5;}
        else {
            bool fresh=Now()-Number("updated")<5;
            string run=Value("selected")+"/"+Value("run");if(lastRun!=run){particles.Clear();bottles=0;emission=0;lastRun=run;}
            working=fresh&&Flag("working");emitting=fresh&&Flag("emitting");seconds=Number("seconds");
            if(working)bottles=(int)Number("bottles");lanes=(int)Number("lanes");status=fresh?Value("status"):"数据连接暂停 · 休息一下";
        }
        float end=408,speed=seconds>0?(end-250)/(float)seconds:0;
        if(emitting&&speed>0&&lanes>0){emission+=speed*dt;while(emission>=31){emission-=31;for(int lane=0;lane<Math.Min(5,lanes);lane++){int row=LaneOrder[lane];float start=RowStart(row);particles.Add(new Particle{X=start,Y=RowY(row),Speed=(end-start)/(float)seconds,Row=row});}}}
        foreach(var p in particles){p.X+=(emitting&&seconds>0?(end-RowStart(p.Row))/(float)seconds:p.Speed)*dt;}particles.RemoveAll(p=>p.X>end);
        if(!working&&particles.Count==0)bottles=0;
        if(visibleScene&&!dragging)Render();
    }
    void Render(){
        if(!loaded||Width<1||Height<1)return;
        var image=new Bitmap(Width,Height,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(image)){
            g.Clear(Color.Transparent);g.ScaleTransform((float)scale,(float)scale);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.SmoothingMode=SmoothingMode.AntiAlias;
            Image sprite=working?work:rest;
            float bob=working?(float)Math.Sin(Now()*5)*1.1f:0;
            // Five stable staggered slots, all inside the crocodile's head/foot height.
            float[,] slots={{0,94,.08f},{0,130,-.12f},{0,166,-.08f},{23,76,.1f},{27,151,.06f}};
            for(int i=0;i<Math.Min(5,bottles);i++){
                var saved=g.Save();g.TranslateTransform(FrontEdge(slots[i,1])-15-slots[i,0],slots[i,1]);g.RotateTransform(slots[i,2]*180/(float)Math.PI);
                g.DrawImage(bottle,new RectangleF(-10,-22,20,30),new RectangleF(274,111,706,1038),GraphicsUnit.Pixel);g.Restore(saved);
            }
            // Soft connecting wakes and travelling dashes keep sparse particles
            // readable as a flow without covering the original cartoon sprite.
            for(int row=0;row<5;row++){
                var lane=particles.FindAll(p=>p.Row==row);
                if(lane.Count==0)continue;
                lane.Sort((a,b)=>a.X.CompareTo(b.X));
                float strength=Math.Min(1,lane[0].Speed/200f);
                using(var wake=new Pen(Color.FromArgb((int)(30+strength*35),176,130,62),2f)){
                    wake.StartCap=LineCap.Round;wake.EndCap=LineCap.Round;
                    for(int i=1;i<lane.Count;i++){
                        float from=lane[i-1].X+7,to=lane[i].X-8;
                        if(to>from&&to-from<80)g.DrawLine(wake,from,lane[i].Y,to,lane[i].Y);
                    }
                }
                foreach(var p in lane){
                    float length=9+strength*15;
                    using(var streak=new Pen(Color.FromArgb((int)(75+strength*65),174,119,47),1.6f)){
                        streak.StartCap=LineCap.Round;streak.EndCap=LineCap.Round;
                        g.DrawLine(streak,p.X-10-length,p.Y-3,p.X-11,p.Y-3);
                        g.DrawLine(streak,p.X-9-length*.65f,p.Y+3,p.X-12,p.Y+3);
                    }
                }
                // These highlights move along the connecting wake, rather than
                // blinking in place; they remain behind the character below.
                using(var glint=new Pen(Color.FromArgb(125,221,178,92),2f)){
                    glint.StartCap=LineCap.Round;glint.EndCap=LineCap.Round;
                    float origin=RowStart(row),limit=lane[lane.Count-1].X-8;
                    float phase=(float)((Now()*lane[0].Speed)%35);
                    for(float x=origin-35+phase;x<limit;x+=35){
                        float a=Math.Max(origin,x),b=Math.Min(limit,x+7);
                        if(b>a)g.DrawLine(glint,a,RowY(row),b,RowY(row));
                    }
                }
            }
            foreach(var p in particles)g.DrawImage(poop,new RectangleF(p.X-10,p.Y-6,20,12),new RectangleF(337,365,770,478),GraphicsUnit.Pixel);
            // Paint the character last so new particles emerge from behind it.
            g.DrawImage(sprite,new RectangleF(125,35+bob,164,164));
        }
        IntPtr screen=GetDC(IntPtr.Zero),dc=CreateCompatibleDC(screen),bitmap=image.GetHbitmap(Color.FromArgb(0)),old=SelectObject(dc,bitmap);
        try{var pos=new PointNative(Left,Top);var size=new SizeNative(Width,Height);var source=new PointNative(0,0);var blend=new Blend{Op=0,Flags=0,Alpha=255,Format=1};if(!UpdateLayeredWindow(Handle,screen,ref pos,ref size,dc,ref source,0,ref blend,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
        finally{SelectObject(dc,old);DeleteObject(bitmap);DeleteDC(dc);ReleaseDC(IntPtr.Zero,screen);}
        if(frame!=null)frame.Dispose();frame=image;
    }
    protected override void WndProc(ref Message m){
        if(m.Msg==0x84&&frame!=null){long value=m.LParam.ToInt64();var p=PointToClient(new Point((short)(value&65535),(short)((value>>16)&65535)));if(p.X>=0&&p.Y>=0&&p.X<frame.Width&&p.Y<frame.Height&&frame.GetPixel(p.X,p.Y).A<15){m.Result=new IntPtr(-1);return;}}
        base.WndProc(ref m);
    }
    [STAThread] static void Main(string[] args){
        bool created;using(var mutex=new System.Threading.Mutex(true,"Local\\CrocTokenDesktop-"+args[0].GetHashCode(),out created)){
            if(!created)return;SetProcessDPIAware();Application.EnableVisualStyles();
            try{Application.Run(new CrocDesktop(args[0],args[1],Array.IndexOf(args,"--demo")>=0,Array.IndexOf(args,"--smoke")>=0));}
            catch(Exception e){File.WriteAllText(Path.Combine(args[1],"desktop-error.txt"),e.ToString());MessageBox.Show(e.Message,"桌面鳄鱼启动失败");}
        }
    }
}
