using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;

namespace Selene
{
    // Playnite's dialogs as add-ons see them (IPlayniteAPI.Dialogs), with one change: a progress task (Extra Metadata
    // downloading logos, trailers...) runs behind Selene's notification card instead of Playnite's progress window. That
    // window was built and shown for every task and then hidden by Selene; building it froze everything moving for a moment
    // (the stutter just before each card). Everything else goes to Playnite's own dialogs unchanged; when the card can't be
    // used (switched off, Playnite minimised) the task goes to Playnite too.
    public class SeleneDialogs : IDialogsFactory
    {
        private readonly IDialogsFactory inner;
        private SeleneDialogs(IDialogsFactory inner) { this.inner = inner; }

        // the task's progress, read by the card the way it reads Playnite's window (a ProgressArgs property)
        public class ProgressHolder { public GlobalProgressActionArgs ProgressArgs { get; set; } }

        // every add-on's own copy of the API (Playnite hands each its own: Extra Metadata's went straight to Playnite's window)
        public static void Install(IPlayniteAPI api)
        {
            int done = InstallOn(api) ? 1 : 0;
            try
            {
                foreach (var plugin in api.Addons.Plugins)
                    for (var t = plugin.GetType(); t != null; t = t.BaseType)
                        foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                            if (typeof(IPlayniteAPI).IsAssignableFrom(f.FieldType) && InstallOn(f.GetValue(plugin) as IPlayniteAPI)) done++;
            }
            catch (Exception ex) { HomeNav.Log("dialogs: add-ons: " + ex.Message); }
            // and Playnite's own tasks (add-on updates, backups, imports from its menus): the app, its parts, the main view,
            // and the handler its dialogs go through
            int own = 0;
            try
            {
                var appType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Playnite.PlayniteApplication", false)).FirstOrDefault(t => t != null);
                var appProp = appType != null ? appType.GetProperty("Current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
                var app = appProp != null ? appProp.GetValue(null, null) : null;
                if (app != null)
                {
                    own += ReplaceIn(app);
                    foreach (var part in FieldsOf(app))
                        if (part != null && (part.GetType().Namespace ?? "").StartsWith("Playnite")) own += ReplaceIn(part);
                }
                var main = Application.Current != null ? Application.Current.MainWindow : null;
                if (main != null && main.DataContext != null) own += ReplaceIn(main.DataContext);
                var handlerType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Playnite.Dialogs", false)).FirstOrDefault(t => t != null);
                var handler = handlerType != null ? handlerType.GetProperty("DialogsHandler", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public) : null;
                var setHandler = handlerType != null ? handlerType.GetMethod("SetHandler", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public) : null;
                var current = handler != null ? handler.GetValue(null, null) as IDialogsFactory : null;
                if (current != null && !(current is SeleneDialogs) && setHandler != null) { setHandler.Invoke(null, new object[] { new SeleneDialogs(current) }); own++; }
            }
            catch (Exception ex) { HomeNav.Log("dialogs: Playnite's own: " + ex.Message); }
            HomeNav.Log("dialogs: progress tasks run behind Selene's card (" + done + " API copies, " + own + " of Playnite's own)");
        }

        private static IEnumerable<object> FieldsOf(object target)
        {
            var list = new List<object>();
            for (var t = target.GetType(); t != null; t = t.BaseType)
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                    if (!f.FieldType.IsValueType) { try { list.Add(f.GetValue(target)); } catch { } }
            return list;
        }

        // every dialogs field of this object wrapped (once); how many
        private static int ReplaceIn(object target)
        {
            int n = 0;
            for (var t = target.GetType(); t != null; t = t.BaseType)
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (!typeof(IDialogsFactory).IsAssignableFrom(f.FieldType)) continue;
                    var current = f.GetValue(target) as IDialogsFactory;
                    if (current == null || current is SeleneDialogs) continue;
                    try { f.SetValue(target, new SeleneDialogs(current)); n++; } catch { }
                }
            return n;
        }

        private static bool InstallOn(IPlayniteAPI api)
        {
            try
            {
                if (api == null || api.Dialogs is SeleneDialogs) return false;
                for (var t = api.GetType(); t != null; t = t.BaseType)
                {
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    {
                        if (!typeof(IDialogsFactory).IsAssignableFrom(f.FieldType)) continue;
                        var current = f.GetValue(api) as IDialogsFactory;
                        if (current == null || current is SeleneDialogs) continue;
                        f.SetValue(api, new SeleneDialogs(current));
                        return true;
                    }
                }
            }
            catch (Exception ex) { HomeNav.Log("dialogs: " + ex.Message); }
            return false;
        }

        private static bool Mine(GlobalProgressOptions options)
        {
            if (HomeNav.Quitting) return false;   // Playnite on its way out: its own "Exiting" screen, not a card
            var app = Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return false;
            // Playnite's shutdown passes the raw key "LOCClosingPlaynite"; running it inside our nested frame froze the exit
            var text = options != null ? options.Text ?? "" : "";
            foreach (var word in new[] { "Closing", "Exiting", "Shutting" })
                if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return Features.On("dialogs") && Features.On("progresstoast") && ProgressToast.Usable();
        }

        // asked from another thread (an add-on reacting to a library update): on the UI thread, as Playnite's own does it
        private static GlobalProgressResult OnUi(Func<GlobalProgressResult> run)
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher.CheckAccess()) return run();
            return app.Dispatcher.Invoke(run);
        }

        public GlobalProgressResult ActivateGlobalProgress(Action<GlobalProgressActionArgs> progresAction, GlobalProgressOptions progressOptions)
        {
            return OnUi(() => Mine(progressOptions) ? Run(args => Task.Run(() => progresAction(args)), progressOptions) : inner.ActivateGlobalProgress(progresAction, progressOptions));
        }

        public GlobalProgressResult ActivateGlobalProgress(Func<GlobalProgressActionArgs, Task> progresAction, GlobalProgressOptions progressOptions)
        {
            return OnUi(() => Mine(progressOptions) ? Run(args => Task.Run(() => progresAction(args)), progressOptions) : inner.ActivateGlobalProgress(progresAction, progressOptions));
        }

        // the task on a worker thread; meanwhile the UI keeps running (a nested message loop, as Playnite's own modal window
        // does), and the caller gets its answer once the task has finished
        private static GlobalProgressResult Run(Func<GlobalProgressActionArgs, Task> start, GlobalProgressOptions options)
        {
            var dispatcher = Application.Current.Dispatcher;
            var cancel = new CancellationTokenSource();
            var args = new GlobalProgressActionArgs(SynchronizationContext.Current ?? new DispatcherSynchronizationContext(dispatcher), dispatcher, cancel.Token);
            if (options != null) { args.Text = options.Text; args.IsIndeterminate = options.IsIndeterminate; }
            var holder = new ProgressHolder { ProgressArgs = args };
            Exception error = null;
            // background work (library update, an add-on's downloads) runs quietly unless its cards are wanted
            if (Features.On("taskcards")) { try { ProgressToast.ShowTask(holder); } catch (Exception ex) { HomeNav.Log("notification: " + ex.Message); } }
            else HomeNav.Log("task in the background: " + (options != null ? options.Text : "?"));
            var frame = new DispatcherFrame();
            Task task = null;
            try { task = start(args); }
            catch (Exception ex) { error = ex; }
            if (task != null)
                task.ContinueWith(t =>
                {
                    if (t.Exception != null) error = t.Exception.InnerException ?? t.Exception;
                    frame.Continue = false;
                }, TaskScheduler.Default);
            else frame.Continue = false;
            if (frame.Continue) Dispatcher.PushFrame(frame);
            ProgressToast.EndTask(holder);
            if (error != null) HomeNav.Log("progress task failed: " + error.Message);
            return new GlobalProgressResult(true, false, error);
        }

        // ---- everything else: Playnite's own ----
        public MessageBoxResult ShowErrorMessage(string messageBoxText) { return inner.ShowErrorMessage(messageBoxText); }
        public MessageBoxResult ShowErrorMessage(string messageBoxText, string caption) { return inner.ShowErrorMessage(messageBoxText, caption); }
        public MessageBoxResult ShowMessage(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) { return inner.ShowMessage(messageBoxText, caption, button, icon); }
        public MessageBoxResult ShowMessage(string messageBoxText, string caption, MessageBoxButton button) { return inner.ShowMessage(messageBoxText, caption, button); }
        public MessageBoxResult ShowMessage(string messageBoxText, string caption) { return inner.ShowMessage(messageBoxText, caption); }
        public MessageBoxResult ShowMessage(string messageBoxText) { return inner.ShowMessage(messageBoxText); }
        public MessageBoxOption ShowMessage(string messageBoxText, string caption, MessageBoxImage icon, List<MessageBoxOption> options) { return inner.ShowMessage(messageBoxText, caption, icon, options); }
        public string SelectFolder() { return inner.SelectFolder(); }
        public string SelectFolder(string initialDirectory) { return inner.SelectFolder(initialDirectory); }
        public string SelectFile(string filter) { return inner.SelectFile(filter); }
        public string SelectFile(string filter, string initialDirectory) { return inner.SelectFile(filter, initialDirectory); }
        public List<string> SelectFiles(string filter) { return inner.SelectFiles(filter); }
        public List<string> SelectFiles(string filter, string initialDirectory) { return inner.SelectFiles(filter, initialDirectory); }
        public string SelectIconFile() { return inner.SelectIconFile(); }
        public string SelectIconFile(string initialDirectory) { return inner.SelectIconFile(initialDirectory); }
        public string SelectImagefile() { return inner.SelectImagefile(); }
        public string SelectImagefile(string initialDirectory) { return inner.SelectImagefile(initialDirectory); }
        public string SaveFile(string filter) { return inner.SaveFile(filter); }
        public string SaveFile(string filter, string initialDirectory) { return inner.SaveFile(filter, initialDirectory); }
        public string SaveFile(string filter, bool promptOverwrite) { return inner.SaveFile(filter, promptOverwrite); }
        public string SaveFile(string filter, bool promptOverwrite, string initialDirectory) { return inner.SaveFile(filter, promptOverwrite, initialDirectory); }
        public StringSelectionDialogResult SelectString(string messageBoxText, string caption, string defaultInput) { return inner.SelectString(messageBoxText, caption, defaultInput); }
        public StringSelectionDialogResult SelectString(string messageBoxText, string caption, string defaultInput, List<MessageBoxToggle> options) { return inner.SelectString(messageBoxText, caption, defaultInput, options); }
        public void ShowSelectableString(string messageBoxText, string caption, string defaultInput) { inner.ShowSelectableString(messageBoxText, caption, defaultInput); }
        public ImageFileOption ChooseImageFile(List<ImageFileOption> files, string caption, double itemWidth, double itemHeight) { return inner.ChooseImageFile(files, caption, itemWidth, itemHeight); }
        public GenericItemOption ChooseItemWithSearch(List<GenericItemOption> items, Func<string, List<GenericItemOption>> searchFunction, string defaultSearch, string caption) { return inner.ChooseItemWithSearch(items, searchFunction, defaultSearch, caption); }
        public Window CreateWindow(WindowCreationOptions options) { return inner.CreateWindow(options); }
        public Window GetCurrentAppWindow() { return inner.GetCurrentAppWindow(); }
    }
}
