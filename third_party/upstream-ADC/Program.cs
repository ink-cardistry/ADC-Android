namespace ArcaeaDarkApkCreator;

internal static class Program
{
    private static int Main()
    {
        try
        {
            new App().Run();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                ConsoleUi.Fail("发生未处理的错误：" + ex.Message);
                ConsoleUi.Dim(ex.ToString());
            }
            catch
            {
                Console.Error.WriteLine(ex);
            }
            return 1;
        }
    }
}
