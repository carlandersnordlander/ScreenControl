namespace SkarmStyrning.Client;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var single = new Mutex(true, "SkarmStyrningKlient-en-instans", out bool first);
        if (!first) return; // klienten körs redan (ligger i systemfältet)

        ApplicationConfiguration.Initialize();
        var form = new ClientForm();
        if (args.Contains("--minimized"))
        {
            // Starta dolt i systemfältet.
            form.WindowState = FormWindowState.Minimized;
            form.ShowInTaskbar = false;
            form.Load += (_, _) => { form.Hide(); form.ShowInTaskbar = true; };
        }
        Application.Run(form);
    }
}
