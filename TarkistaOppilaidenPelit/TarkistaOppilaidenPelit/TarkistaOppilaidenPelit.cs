using System;
using System.Collections.Generic;
using Jypeli;
using Jypeli.Assets;
using Jypeli.Controls;
using Jypeli.Effects;
using Jypeli.Widgets;
using Microsoft.Xna.Framework.Graphics;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text;

// Oppilas, PelinNimi, Repo, Lista checkoutattavista tiedostoista/kansioista, Solutiontiedosto
class GameRecord 
{ 
  public string Author;
  public string GameName;
  public string SVNRepo;
  public List<string> ToFetch;
  public string Solution;
}

class User32
{
    [DllImport("user32.dll")]
    public static extern void SetWindowPos(uint Hwnd, int Level, int X, int Y, int W, int H, uint Flags);
}

/*
* For checking out we use
* > svn checkout <repo> <author_folder> --depth empty
* > cd <author_folder>/trunk
* > svn up <files/folders_you_want>
*/
public class TarkistaOppilaidenPelit : Game
{
    static int HWND_TOP = 0;
    static int HWND_TOPMOST = -1;
    static int MAX_TIMEOUT = 30000; // wait for 15 sec
    static int SHOW_THIS_MANY_MESSAGES = 10; // Message window does not have scroll. Show only this many recent messages.

    string SVN_CLI_EXE = @"C:\Temp\svn-win32-1.8.8\bin\svn.exe";
    string MSBUILD_EXE = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MsBuild.exe";
    //double TASK_COMPLETION_POLL_INTERVAL = 2.0;
    double PROCESS_CHECK_INTERVAL = 1.0;
    int MAX_GAME_RUN_TIME = 3000;

    List<Tuple<GameRecord, Task>> taskQueue;
    List<GameRecord> listOfGames;
    Process activeCliProcess;
    bool processing = false;
    bool paused = false;
    bool topmost = false;

    Mutex stateQueueMutex = new Mutex();
    Dictionary<string, List<string>> detailedMessages = new Dictionary<string, List<string>>();
    Queue<string> messageQueue = new Queue<string>();
    Queue<Tuple<GameRecord, Task, Status>> stateQueue = new Queue<Tuple<GameRecord, Task, Status>>();


    Thread processingThread;

    enum Task
    {
        Checkout,
        UpdateListed,
        Compile,
        RunGame,
        None
    }

    enum Status
    {
        Wait,
        OK,
        Fail,
        NA
    }

    Dictionary<Task, string> taskToLabel = new Dictionary<Task, string>()
    {
        {Task.Checkout, "Alustus"},
        {Task.UpdateListed, "Haku"},
        {Task.Compile, "Kääntö"},
        {Task.RunGame, "Pelaa"},
    };
   
    

    public override void Begin()
    {
        SetWindowSize(1024, 768);
        if (topmost)
            SetWindowTopmost(topmost);
        
        IsMouseVisible = true;

        PhoneBackButton.Listen(ConfirmExit, "Lopeta peli");
        Keyboard.Listen(Key.Escape, ButtonState.Pressed, ConfirmExit, "Lopeta ohjelma");
        Keyboard.Listen(Key.P, ButtonState.Pressed, OnPauseProcess, "Pistä tauolle");
        Keyboard.Listen(Key.T, ButtonState.Pressed, OnToggleTopmost, "Piilota pelit taustalle");
        Keyboard.Listen(Key.H, ButtonState.Pressed, ShowControlHelp, "Näytä tämä ohje uudelleen");

        Mouse.Listen(MouseButton.Left, ButtonState.Released, OnMouseClicked, "Klikkaa palloista lisätietoja");
        ShowControlHelp();

        // TODO: There should be other ways to get this, a plain text file for example?
        //  but for now hard coded list will do.
        listOfGames = GetHardCodedList();

        // Simple validation
        List<string> authors = listOfGames.Select(ot => ot.Author).Distinct().ToList();
        if (listOfGames.Count != authors.Count)
            throw new ArgumentException("Tekijöiden pitää olla yksilöllisiä");

        double aspect = Screen.Width/Screen.Height;
        int rows = (int)Math.Ceiling( Math.Sqrt(listOfGames.Count)/aspect );
        int cols = (int)Math.Floor( Math.Sqrt(listOfGames.Count)*aspect );

        // Create indicators
        taskQueue = new List<Tuple<GameRecord, Task>>();
        for (int i = 0; i < listOfGames.Count; i++)
        {
            int row = i / cols + 1;
            int col = i % cols + 1;

            var record = listOfGames[i];
            detailedMessages.Add(record.Author, new List<string>());

            var indicator = new GameObject(40*2, 40*2, Shape.Circle);
            indicator.Color = Color.Gray;
            indicator.Tag = record.Author + "_indicator";
    
            // +2 is for margins        
            indicator.X = Screen.Left + ((Screen.Right - Screen.Left) / (cols+1)) * col;
            indicator.Y = Screen.Top - ((Screen.Top - Screen.Bottom) / (rows+1)) * row;
            Add(indicator);

            var nameLabel = new Label(120, 40);
            //nameLabel.Font = Font.DefaultLargeBold;
            nameLabel.Text = record.Author;
            nameLabel.Position = new Vector(indicator.X, indicator.Y + 60);
            Add(nameLabel);

            var statusLabel = new Label(120, 40);
            //nameLabel.Font = Font.DefaultLargeBold;
            statusLabel.Tag = record.Author + "_status";
            statusLabel.Text = "odota";
            statusLabel.Position = new Vector(indicator.X, indicator.Y - 60);
            Add(statusLabel);

            // Always 1st try (or verify) that the files have been checked out
            taskQueue.Add( new Tuple<GameRecord, Task>(record, Task.Checkout) );
        }

        Exiting += KillTaskProcessorThread;

        ThreadedTaskListProcessor();
    }

    #region KeypressHandlers
    void OnPauseProcess()
    {
        paused = !paused;
    }

    void OnToggleTopmost()
    {
        topmost = !topmost;
        SetWindowTopmost(topmost);
    }

    void OnMouseClicked()
    {
        GameObject clickTgt = GetObjectAt(Mouse.PositionOnWorld);
        if (clickTgt != null && clickTgt.Tag is string)
        {
            string stag = clickTgt.Tag as string;
            if (stag.Contains("_indicator"))
            {
                string author = stag.Replace("_indicator", "");
                if (stateQueueMutex.WaitOne(3000))
                {
                    StringBuilder allMsgs = new StringBuilder();
                    // MessageWindow does not have vertical scroll, so limit to 20 or so last messages
                    for (int i = Math.Max(0, detailedMessages[author].Count-SHOW_THIS_MANY_MESSAGES);
                             i < detailedMessages[author].Count;
                             i++)
			        {
                        allMsgs.Append(detailedMessages[author][i]+ "\n");
	                }
                    stateQueueMutex.ReleaseMutex();

                    // Show error messages.
                    MessageWindow ikkuna = new MessageWindow(allMsgs.ToString());
                    Add(ikkuna);
                }
            }

        }
    }
    #endregion

    void SetWindowTopmost(bool topmost)
    {
        int screenHt = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height;
        int screenWt = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width;
        if (topmost)
        {
            User32.SetWindowPos((uint)this.Window.Handle, HWND_TOPMOST, 0, 0, screenWt, screenHt, 0);
        }
        else
        {
            User32.SetWindowPos((uint)this.Window.Handle, HWND_TOP, 0, 0, screenWt, screenHt, 0);
        }
    }

    /// <summary>
    /// Update processes asynchronous messaging and state changes from the worker (processing) thread.
    /// </summary>
    /// <param name="time"></param>
    protected override void Update(Time time)
    {
        base.Update(time);
        var hasOne = stateQueueMutex.WaitOne(10);
        if (hasOne)
        {
            while (messageQueue.Count > 0)
            {
                string message = messageQueue.Dequeue();
                MessageDisplay.Add(message);
            }

            while (stateQueue.Count > 0)
            {
                var stateChange = stateQueue.Dequeue();
                var author = stateChange.Item1.Author;

                var indicator = GetObjectsWithTag(author + "_indicator").First();
                var label = GetObjectsWithTag(author + "_status").First() as Label;
                label.Text = taskToLabel[stateChange.Item2];

                switch (stateChange.Item3)
                {
                    case Status.Wait:
                        indicator.Color = Color.Yellow;        
                        break;
                    case Status.OK:
                        if (stateChange.Item2==Task.RunGame)
                            indicator.Color = Color.Green;
                        else
                            indicator.Color = Color.Yellow;
                        break;
                    case Status.Fail:
                        indicator.Color = Color.Red;
                        break;
                    default:
                        break;
                }
            }
            stateQueueMutex.ReleaseMutex();
        }
    }

    

    #region TaskProcessing
    private void ThreadedTaskListProcessor()
    {
        processing = false;
        processingThread = new Thread(new ThreadStart(ProcessTaskList));

        // Start the thread
        processingThread.Start();

        // Spin for a while waiting for the started thread to become
        // alive:
        while (!processingThread.IsAlive) ;
    }
    private void KillTaskProcessorThread()
    {
        if (processingThread!=null)
            processingThread.Abort();
    }


    void ProcessTaskList()
    {
        while (true)
        {
            if (!processing)
                System.Threading.Thread.Sleep( (int)(PROCESS_CHECK_INTERVAL*1000) );

            while (paused)
                System.Threading.Thread.Sleep((int)(PROCESS_CHECK_INTERVAL * 1000));

            if (taskQueue.Count == 0)
                break;

            var task = taskQueue[0];
            taskQueue.RemoveAt(0);
            string msg;
            switch (task.Item2)
            {   
                case Task.Checkout:
                    stateQueueMutex.WaitOne();
                    msg = "Checking out for " + task.Item1.Author;
                    messageQueue.Enqueue(msg);
                    detailedMessages[task.Item1.Author].Add("\n" + msg + "\n");
                    stateQueueMutex.ReleaseMutex();
                    ProcessCheckoutRepo(task.Item1);
                    break;
                case Task.UpdateListed:
                    stateQueueMutex.WaitOne();
                    msg = "Updating files from " + task.Item1.Author;
                    messageQueue.Enqueue(msg);
                    detailedMessages[task.Item1.Author].Add("\n" + msg + "\n");
                    stateQueueMutex.ReleaseMutex();
                    ProcessUpdateListed(task.Item1);
                    break;
                case Task.Compile:
                    stateQueueMutex.WaitOne();
                    msg = "Compiling project of " + task.Item1.Author;
                    messageQueue.Enqueue(msg);
                    detailedMessages[task.Item1.Author].Add("\n" + msg + "\n");
                    stateQueueMutex.ReleaseMutex();
                    ProcessCompile(task.Item1);
                    break;
                case Task.RunGame:
                    stateQueueMutex.WaitOne();
                    msg = "Running game of " + task.Item1.Author;
                    messageQueue.Enqueue(msg);
                    detailedMessages[task.Item1.Author].Add("\n" + msg + "\n");
                    stateQueueMutex.ReleaseMutex();
                    ProcessRunGame(task.Item1);
                    break;
                default:
                    break;
            }
            processing = false;
        }
    }

    void ProcessCheckoutRepo(GameRecord record)
    {
        // Directory existance implies existing checkout
        if (Directory.Exists(record.Author))
        {
            taskQueue.Add(new Tuple<GameRecord, Task>(record, Task.UpdateListed));
        }
        else
        {
            Directory.CreateDirectory(record.Author);
            Task currentTask = Task.Checkout;
            Task nextTask = Task.UpdateListed;
            string command = String.Format("\"{0}\" co {1} \"{2}\" --depth empty", SVN_CLI_EXE, record.SVNRepo, Path.Combine(Directory.GetCurrentDirectory(), record.Author));

            GenericProcessor(record, currentTask, nextTask, command);
        }
    }

    /// <summary>
    /// Update the files/folders in the record from svn. A new batch of tasks to update each is added to the queue.
    /// Fail: Does not update for some reason.
    /// OK: Updated w/o problems.
    /// After: Task.Compile
    /// </summary>
    void ProcessUpdateListed(GameRecord record)
    {
        Task currentTask = Task.UpdateListed;
            
        foreach (var toUpdate in record.ToFetch)
        {
            Task nextTask = Task.None;
            bool addRetry = false;
            // The success of the last update will determine if to try again or if to try and compile.
            if (toUpdate == record.ToFetch.Last())
            {
                nextTask = Task.Compile;
                addRetry = true;    
            }
            string command = String.Format("\"{0}\" up \"{1}\"", SVN_CLI_EXE, Path.Combine(Directory.GetCurrentDirectory(), record.Author, toUpdate));
            GenericProcessor(record, currentTask, nextTask, command, -1, addRetry);
        }
    }

    /// <summary>
    /// Compile the .sln with msbuild.
    /// Fail: Does not compile for some reason.
    /// OK: Game compiles without error.
    /// After: Task.UpdateListed
    /// </summary>
    void ProcessCompile(GameRecord record)
    {
        Task currentTask = Task.Compile;
        Task nextTask = Task.RunGame;
        string command = String.Format("\"{0}\" /nologo /noconlog \"{1}\"", MSBUILD_EXE, Path.Combine(Directory.GetCurrentDirectory(), record.Author, record.Solution));
        GenericProcessor(record, currentTask, nextTask, command);
    }

    /// <summary>
    /// Run game for the duration of MAX_GAME_RUN_TIME and of no problems arise, kill it. 
    /// Fail: game does not start or crashes
    /// OK: Game runs fone for MAX_GAME_RUN_TIME 
    /// After: Task.UpdateListed
    /// </summary>
    void ProcessRunGame(GameRecord record)
    {
        string gameExeName = "";
        foreach (string file in Directory.EnumerateFiles(
            record.Author, "*.exe", SearchOption.AllDirectories))
        {
            if (gameExeName == "")
            {
                gameExeName = file;
            }
            else
            {
                stateQueueMutex.WaitOne();
                string msg = "Multiple game exes for the game, using " + gameExeName;
                messageQueue.Enqueue(msg);
                detailedMessages[record.Author].Add(msg);
                stateQueueMutex.ReleaseMutex();
                break;
            }
        }
        if (gameExeName == "")
        {
            // No game to run. Skip to update.
            taskQueue.Add(new Tuple<GameRecord, Task>(record, Task.UpdateListed));
        }
        else
        {
            Task currentTask = Task.RunGame;
            Task nextTask = Task.UpdateListed;
            string command = String.Format("\"{0}\"", gameExeName);
            GenericProcessor(record, currentTask, nextTask, command, MAX_GAME_RUN_TIME);
        }
    }



    private Status GenericProcessor(GameRecord record, Task currentTask, Task nextTask, string command, int runTimeout = -1, bool addRetry = true)
    {
        string msg;
        Status returnStatus = Status.NA;
        if (activeCliProcess == null)
        {
            stateQueueMutex.WaitOne();
            stateQueue.Enqueue(new Tuple<GameRecord, Task, Status>(record, currentTask, Status.Wait));
            stateQueueMutex.ReleaseMutex();

            // split
            string exepart = command.Substring(0, command.IndexOf(".exe\"")+5);
            string argpart = command.Substring(command.IndexOf(".exe\"")+5);

            activeCliProcess = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            startInfo.FileName = exepart;
            startInfo.Arguments = argpart;

            /*
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/C "+command;
            */

            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardOutput = true;

            activeCliProcess.StartInfo = startInfo;
            activeCliProcess.Start();

            //messageQueueMutex.WaitOne();
            //messageQueue.Enqueue("Running command " + command);
            //messageQueueMutex.ReleaseMutex();

            if (runTimeout == -1)
            {
                if (!activeCliProcess.WaitForExit(MAX_TIMEOUT))
                {
                    activeCliProcess.Kill();
                    stateQueueMutex.WaitOne();
                    msg = "Process timeout: killed.";
                    messageQueue.Enqueue(msg);
                    detailedMessages[record.Author].Add(msg);
                    stateQueueMutex.ReleaseMutex();
                    Thread.Sleep(200);
                }
            }
            else
            {
                activeCliProcess.WaitForExit(runTimeout);
            }
        }
        if (activeCliProcess.HasExited)
        {

            // THIS is probably CMD.exe exitcode
            if (activeCliProcess.ExitCode == 0)
            {
                stateQueueMutex.WaitOne();
                msg = "Process exited with CODE 0.";
                messageQueue.Enqueue(msg);
                detailedMessages[record.Author].Add("\n"+msg);

                StreamReader sr = activeCliProcess.StandardOutput;
                while (!sr.EndOfStream)
                {
                    String s = sr.ReadLine();
                    if (s != "")
                    {
                        Trace.WriteLine(s);
                        detailedMessages[record.Author].Add(s);
                    }
                }

                stateQueue.Enqueue(new Tuple<GameRecord, Task, Status>(record, currentTask, Status.OK));
                returnStatus = Status.OK;

                stateQueueMutex.ReleaseMutex();


                // TODO: Check if it was successfull, //  update light bulb state and 
                if (nextTask != Task.None)
                    taskQueue.Insert(0, new Tuple<GameRecord, Task>(record, nextTask));
            }
            else
            {
                stateQueueMutex.WaitOne();
                msg = "Process exited with CODE 1.";
                messageQueue.Enqueue(msg);
                detailedMessages[record.Author].Add("\n" + msg);

                
                StreamReader sro = activeCliProcess.StandardOutput;
                while (!sro.EndOfStream)
                {
                    String s = sro.ReadLine();
                    if (s != "")
                    {
                        Trace.WriteLine(s);
                        detailedMessages[record.Author].Add(s);
                    }
                }
                StreamReader sre = activeCliProcess.StandardError;
                while (!sre.EndOfStream)
                {
                    String s = sre.ReadLine();
                    if (s != "")
                    {
                        Trace.WriteLine(s);
                        detailedMessages[record.Author].Add(s);
                    }
                }

                stateQueue.Enqueue(new Tuple<GameRecord, Task, Status>(record, currentTask, Status.Fail));
                returnStatus = Status.Fail;

                stateQueueMutex.ReleaseMutex();

                if (addRetry)
                    taskQueue.Add(new Tuple<GameRecord, Task>(record, currentTask));
            }
        }
        else
        {
            activeCliProcess.Kill();
            stateQueueMutex.WaitOne();
            msg = "Process killed as scheduled.";
            messageQueue.Enqueue(msg);
            detailedMessages[record.Author].Add(msg);
            stateQueue.Enqueue(new Tuple<GameRecord, Task, Status>(record, currentTask, Status.OK));
            returnStatus = Status.OK;
            stateQueueMutex.ReleaseMutex();

            // TODO: Check if it was successfull, //  update light bulb state and 
            if (nextTask != Task.None)
                taskQueue.Add(new Tuple<GameRecord, Task>(record, nextTask));
        }
        activeCliProcess = null;

        return returnStatus;
    }
#endregion

    List<GameRecord> GetHardCodedListJust1()
    {
        // Jos monta oppilasta tekee samaa peliä, käytä nimenä molempia "Jaakko&Jussi"
        //  Ei välejä
        return new List<GameRecord>(){
            new GameRecord(){
                Author="Alex&JoonaR",
                GameName="Zombie Swing",
                SVNRepo="https://github.com/magishark/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"Rope Swing"
                },
                Solution=@"Rope Swing\Rope Swing.sln"},};
    }

    List<GameRecord> GetHardCodedList()
    {
        // Jos monta oppilasta tekee samaa peliä, käytä nimenä molempia "Jaakko&Jussi"
        //  Ei välejä
        return new List<GameRecord>(){
            new GameRecord(){
                Author="AlexJaJoonaR",
                GameName="Zombie Swing",
                SVNRepo="https://github.com/magishark/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"Rope Swing"
                },
                Solution=@"Rope Swing\Rope Swing.sln"},

            /*new GameRecord(){
                Author="Antti-Jussi",
                GameName="?",
                SVNRepo=@"https://github.com/aj-pelikurssi2014/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"Tasohyppelypeli1"
                },
                Solution=@"Tasohyppelypeli1\Tasohyppelypeli1.sln"},*/

            new GameRecord(){
                Author="Atte",
                GameName="Crazy Greg",
                SVNRepo=@"https://github.com/JeesMies00/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"GrazyGreg.sln",
                    @"GrazyGreg"
                },
                Solution=@"GrazyGreg.sln"},

            new GameRecord(){
                Author="Dani",
                GameName="bojoing",
                SVNRepo=@"https://github.com/daiseri45/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"bojoing",
                },
                Solution=@"bojoing\bojoing.sln"},

            new GameRecord(){
                Author="Emil-Aleksi",
                GameName="Rainbow Fly",
                SVNRepo=@"https://github.com/EA99/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"RainbowFly",
                },
                Solution=@"RainbowFly\RainbowFly.sln"},

            new GameRecord(){
                Author="Jere",
                GameName="Suklaakakku",
                SVNRepo=@"https://github.com/jerekop/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"FysiikkaPeli1",
                    @"FysiikkaPeli1.sln",
                },
                Solution=@"FysiikkaPeli1.sln"},

            new GameRecord(){
                Author="Joel",
                GameName="Urhea Sotilas",
                SVNRepo=@"https://github.com/JopezSuomi/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"UrheaSotilas",
                },
                Solution=@"UrheaSotilas\UrheaSotilas.sln"},

            new GameRecord(){
                Author="JoonaK",
                GameName="_insert name here_",
                SVNRepo=@"https://github.com/kytari/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"_Insert name here_",
                    @"_Insert name here_.sln",
                },
                Solution=@"_Insert name here_.sln"},


            new GameRecord(){
                Author="SakuJaJoeli",
                GameName="Flappy derp",
                SVNRepo=@"https://github.com/EXIBEL/sejypeli.git/trunk",
                ToFetch=new List<string>(){
                    @"Falppy derp Saku",
                },
                Solution=@"Falppy derp Saku\Falppy derp Saku.sln"},
        };
    }
}
