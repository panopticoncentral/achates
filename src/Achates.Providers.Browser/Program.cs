using Achates.Providers.Browser.Services;
using Achates.Providers.Browser.Views;
using Terminal.Gui.App;

using var app = Application.Create();
app.Init();

using var httpClient = new HttpClient();
var modelService = new ModelService(httpClient);

app.Run(new MainWindow(modelService));
