Imports System
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Security.Principal
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Web.WebView2.Core

Namespace RobloxLauncher

    Partial Public Class MainWindow
        Inherits Form

        Private WithEvents webView As Microsoft.Web.WebView2.WinForms.WebView2

        ' رقم الإصدار الحالي لبرنامج Launcher
        Private ReadOnly CurrentLauncherVersion As String = "1.0.1"

        ' رابط GitHub API الخاص بـ Releases للمشغّل
        Private Const GitHubApiUrl As String = "https://api.github.com/repos/Maleksayyah/RobloxPortableApp/releases/latest"

        ' Hosted GitHub Pages URL للواجهة
        Private Const GitHubPagesUrl As String = "https://maleksayyah.github.io/RobloxPortableApp/"

        ' مسار مجلد الإصدارات الرئيسي لـ Roblox Portable
        Private ReadOnly BaseVersionsFolder As String = "D:\RobloxPortable\data\programfiles\Roblox\Versions"

        Public Sub New()
            ' التأكد من صلاحيات المسؤول لإنشاء Junctions
            EnsureAdminRights()

            ' إعداد الـ Directory Junctions عند الإقلاع
            SetupJunctions()

            InitializeCustomComponents()
            InitializeWebView()
        End Sub

        Private Sub EnsureAdminRights()
            Dim identity = WindowsIdentity.GetCurrent()
            Dim principal = New WindowsPrincipal(identity)

            If Not principal.IsInRole(WindowsBuiltInRole.Administrator) Then
                Try
                    Dim startInfo As New ProcessStartInfo() With {
                        .FileName = Application.ExecutablePath,
                        .UseShellExecute = True,
                        .Verb = "runas"
                    }
                    Process.Start(startInfo)
                Catch ex As Exception
                    MessageBox.Show("Administrator privileges are required to configure directory junctions.", "Permission Error", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                End Try
                Environment.Exit(0)
            End If
        End Sub

        Private Sub SetupJunctions()
            Dim portableAppDataTarget As String = "D:\RobloxPortable\data\applocal\Roblox"
            Dim portableProgramFilesTarget As String = "D:\RobloxPortable\data\programfiles\Roblox"

            If Not Directory.Exists(portableAppDataTarget) Then Directory.CreateDirectory(portableAppDataTarget)
            If Not Directory.Exists(portableProgramFilesTarget) Then Directory.CreateDirectory(portableProgramFilesTarget)

            Dim localAppDataPath As String = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            Dim robloxLocalAppData As String = Path.Combine(localAppDataPath, "Roblox")
            Dim robloxProgramFiles As String = "C:\Program Files\Roblox"
            Dim robloxProgramFilesX86 As String = "C:\Program Files (x86)\Roblox"

            CreateDirectoryJunction(robloxLocalAppData, portableAppDataTarget)
            CreateDirectoryJunction(robloxProgramFiles, portableProgramFilesTarget)
            CreateDirectoryJunction(robloxProgramFilesX86, portableProgramFilesTarget)
        End Sub

        Private Sub CreateDirectoryJunction(ByVal linkPath As String, ByVal targetPath As String)
            Try
                If Directory.Exists(linkPath) OrElse File.Exists(linkPath) Then
                    Dim attr As FileAttributes = File.GetAttributes(linkPath)
                    If (attr And FileAttributes.ReparsePoint) = FileAttributes.ReparsePoint Then
                        Directory.Delete(linkPath)
                    Else
                        Directory.Delete(linkPath, True)
                    End If
                End If

                Dim cmdInfo As New ProcessStartInfo("cmd.exe", $"/c mklink /J ""{linkPath}"" ""{targetPath}""") With {
                    .CreateNoWindow = True,
                    .UseShellExecute = False,
                    .RedirectStandardError = True,
                    .RedirectStandardOutput = True
                }

                Using proc As Process = Process.Start(cmdInfo)
                    proc.WaitForExit()
                End Using
            Catch ex As Exception
                Debug.WriteLine($"Failed to create junction for {linkPath}: {ex.Message}")
            End Try
        End Sub

        Private Async Sub InitializeWebView()
            Await webView.EnsureCoreWebView2Async(Nothing)
            webView.CoreWebView2.Settings.IsWebMessageEnabled = True

            Await webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.CacheStorage Or CoreWebView2BrowsingDataKinds.DiskCache
            )

            AddHandler webView.CoreWebView2.WebMessageReceived, AddressOf OnWebMessageReceived
            AddHandler webView.CoreWebView2.NavigationCompleted, AddressOf OnNavigationCompleted

            LoadOnlinePage()
        End Sub

        Private Sub LoadOnlinePage()
            Dim cacheBustUrl As String = $"{GitHubPagesUrl}?v={DateTime.Now.Ticks}"
            webView.Source = New Uri(cacheBustUrl)
        End Sub

        Private Sub OnNavigationCompleted(ByVal sender As Object, ByVal e As CoreWebView2NavigationCompletedEventArgs)
            If Not e.IsSuccess Then
                Dim offlineHtml As String = GetOfflineHtml()
                webView.CoreWebView2.NavigateToString(offlineHtml)
            Else
                If webView.Source.ToString().StartsWith(GitHubPagesUrl, StringComparison.OrdinalIgnoreCase) Then
                    ' الفحص المباشر للمسار لمعرفة إذا كانت اللعبة مثبتة أم لا
                    If String.IsNullOrEmpty(FindRobloxExePath()) Then
                        webView.CoreWebView2.PostWebMessageAsString("disable_start")
                    End If
                End If
            End If
        End Sub

        Private Async Sub OnWebMessageReceived(ByVal sender As Object, ByVal e As CoreWebView2WebMessageReceivedEventArgs)
            Dim message As String = e.TryGetWebMessageAsString()

            Select Case message
                Case "launch_game"
                    LaunchRoblox()

                Case "open_website"
                    Me.FormBorderStyle = FormBorderStyle.Sizable
                    Me.MaximizeBox = True
                    Me.WindowState = FormWindowState.Maximized
                    webView.CoreWebView2.Navigate("https://www.roblox.com")

                Case "update_roblox"
                    Await UpdateRobloxInstallerAsync()

                Case "check_for_app_update"
                    Await CheckAndDownloadSetupFromGitHubAsync()

                Case "retry_connection"
                    LoadOnlinePage()
            End Select
        End Sub

        Private Sub MainWindow_Load(ByVal sender As Object, ByVal e As EventArgs) Handles MyBase.Load
            If String.IsNullOrEmpty(FindRobloxExePath()) Then
                Text = "Roblox Launcher - Executable Not Found"
            End If
        End Sub

        ''' <summary>
        ''' البحث عن المسار الأحدث لملف RobloxPlayerBeta.exe ديناميكياً
        ''' </summary>
        Private Function FindRobloxExePath() As String
            Try
                If Not Directory.Exists(BaseVersionsFolder) Then Return Nothing

                Dim targetDir As String = Directory.GetDirectories(BaseVersionsFolder) _
                    .Where(Function(d) Path.GetFileName(d).StartsWith("version-", StringComparison.OrdinalIgnoreCase)) _
                    .OrderByDescending(Function(d) Directory.GetLastWriteTimeUtc(d)) _
                    .FirstOrDefault()

                If targetDir Is Nothing Then Return Nothing

                Dim exePath As String = Path.Combine(targetDir, "RobloxPlayerBeta.exe")
                If File.Exists(exePath) Then Return exePath

                Return Nothing
            Catch ex As Exception
                Debug.WriteLine($"Error finding path: {ex.Message}")
                Return Nothing
            End Try
        End Function

        Private Sub LaunchProcess(ByVal path As String)
            Try
                Process.Start(New ProcessStartInfo(path) With {.UseShellExecute = True})
                Application.Exit()
            Catch ex As Exception
                MessageBox.Show($"Error launching game: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Sub

        Private Sub LaunchRoblox()
            ' فحص المسار فوراً عند الضغط لضمان الدقة
            Dim currentExePath As String = FindRobloxExePath()

            If String.IsNullOrEmpty(currentExePath) OrElse Not File.Exists(currentExePath) Then
                MessageBox.Show("Could not find Roblox installation folder!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            LaunchProcess(currentExePath)
        End Sub

        ''' <summary>
        ''' Update Roblox: البحث عن أحدث Installer وتشغيله وتنظيف المجلد القديم عند الانتهاء
        ''' </summary>
        Private Async Function UpdateRobloxInstallerAsync() As Task
            Try
                If Not Directory.Exists(BaseVersionsFolder) Then
                    MessageBox.Show("Could not find base Roblox Versions directory:" & vbCrLf & BaseVersionsFolder, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
                    Return
                End If

                Dim installerFiles = Directory.GetFiles(BaseVersionsFolder, "RobloxPlayerInstaller.exe", SearchOption.AllDirectories)

                If installerFiles.Length = 0 Then
                    MessageBox.Show("Could not find RobloxPlayerInstaller.exe in versions folder!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
                    Return
                End If

                ' ترتيب الملفات تنازلياً للحصول على أحدث ملف تثبيت
                Dim robloxInstallerPath As String = installerFiles.OrderByDescending(Function(f) File.GetLastWriteTimeUtc(f)).First()
                Dim oldVersionFolder As String = Path.GetDirectoryName(robloxInstallerPath)

                MessageBox.Show("Starting Roblox update...", "Roblox Update", MessageBoxButtons.OK, MessageBoxIcon.Information)

                Dim pInfo As New ProcessStartInfo(robloxInstallerPath) With {
                    .UseShellExecute = True,
                    .Verb = "runas"
                }

                Dim p As Process = Process.Start(pInfo)

                If p IsNot Nothing Then
                    Await Task.Run(Sub() p.WaitForExit())
                End If

                If Directory.Exists(oldVersionFolder) Then
                    Try
                        Await Task.Delay(3000)
                        Directory.Delete(oldVersionFolder, True)
                        MessageBox.Show("Roblox updated and old version folder removed successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information)
                    Catch ex As Exception
                        MessageBox.Show("Updated successfully, but failed to remove old folder: " & ex.Message, "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    End Try
                End If

            Catch ex As Exception
                MessageBox.Show("Error updating Roblox: " & ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Function

        ''' <summary>
        ''' Check For Update: الفحص والتحديث لبرنامج المشغل نفسه عبر GitHub Setup.exe
        ''' </summary>
        Private Async Function CheckAndDownloadSetupFromGitHubAsync() As Task
            Try
                Using client As New HttpClient()
                    client.DefaultRequestHeaders.Add("User-Agent", "RobloxLauncherApp")

                    Dim jsonResponse As String = Await client.GetStringAsync(GitHubApiUrl)
                    Dim latestVersionTag As String = GetJsonValue(jsonResponse, "tag_name").Replace("v", "").Trim()
                    Dim setupDownloadUrl As String = GetSetupDownloadUrl(jsonResponse)

                    If IsNewVersionAvailable(CurrentLauncherVersion, latestVersionTag) Then
                        If String.IsNullOrEmpty(setupDownloadUrl) Then
                            MessageBox.Show("A new version is available, but Setup.exe was not found in the release assets!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                            Return
                        End If

                        Dim choice As DialogResult = MessageBox.Show($"A new launcher update is available ({latestVersionTag})!" & vbCrLf & "Would you like to download and run the setup installer now?",
                                                                     "New Update Available",
                                                                     MessageBoxButtons.YesNo,
                                                                     MessageBoxIcon.Information)

                        If choice = DialogResult.Yes Then
                            Await DownloadAndRunSetup(setupDownloadUrl)
                        End If
                    Else
                        MessageBox.Show("You are running the latest launcher version (" & CurrentLauncherVersion & ").", "No Update", MessageBoxButtons.OK, MessageBoxIcon.Information)
                    End If
                End Using

            Catch ex As Exception
                MessageBox.Show("Failed to check for updates: " & ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Function

        Private Async Function DownloadAndRunSetup(setupUrl As String) As Task
            Try
                Dim tempSetupPath As String = Path.Combine(Path.GetTempPath(), "RobloxPortable_Setup.exe")

                MessageBox.Show("Downloading setup installer...", "Downloading Update", MessageBoxButtons.OK, MessageBoxIcon.Information)

                Using client As New HttpClient()
                    Using stream = Await client.GetStreamAsync(setupUrl)
                        Using fileStream = New FileStream(tempSetupPath, FileMode.Create, FileAccess.Write, FileShare.None)
                            Await stream.CopyToAsync(fileStream)
                        End Using
                    End Using
                End Using

                MessageBox.Show("Setup installer downloaded! Running setup now...", "Download Complete", MessageBoxButtons.OK, MessageBoxIcon.Information)

                Process.Start(New ProcessStartInfo(tempSetupPath) With {
                    .UseShellExecute = True
                })

                Application.Exit()

            Catch ex As Exception
                MessageBox.Show("Error executing setup: " & ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Function

        Private Function GetSetupDownloadUrl(json As String) As String
            Try
                Dim keyPattern As String = """browser_download_url"""
                Dim currentIndex As Integer = 0

                While True
                    Dim index As Integer = json.IndexOf(keyPattern, currentIndex)
                    If index = -1 Then Exit While

                    Dim startIndex As Integer = json.IndexOf(":", index) + 1
                    Dim endIndex As Integer = json.IndexOfAny({","c, "}"c, "]"c}, startIndex)

                    Dim url As String = json.Substring(startIndex, endIndex - startIndex).Trim().Replace("""", "")

                    If url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then
                        Return url
                    End If

                    currentIndex = endIndex
                End While

                Return ""
            Catch
                Return ""
            End Try
        End Function

        Private Function IsNewVersionAvailable(currentVer As String, latestVer As String) As Boolean
            Try
                Dim vCurrent As New Version(currentVer)
                Dim vLatest As New Version(latestVer)
                Return vLatest > vCurrent
            Catch
                Return False
            End Try
        End Function

        Private Function GetJsonValue(json As String, key As String) As String
            Dim keyPattern As String = """" & key & """"
            Dim index As Integer = json.IndexOf(keyPattern)
            If index = -1 Then Return ""

            Dim startIndex As Integer = json.IndexOf(":", index) + 1
            Dim endIndex As Integer = json.IndexOfAny({","c, "}"c, "]"c}, startIndex)

            If endIndex = -1 Then Return ""

            Dim value As String = json.Substring(startIndex, endIndex - startIndex).Trim()
            Return value.Replace("""", "")
        End Function

        Private Function GetOfflineHtml() As String
            Return "<!DOCTYPE html>" &
                   "<html>" &
                   "<head>" &
                   "<meta charset='UTF-8'>" &
                   "<style>" &
                   "  body { background: #0f111a; color: #ffffff; font-family: 'Segoe UI', sans-serif; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; text-align: center; }" &
                   "  .card { background: rgba(22, 26, 42, 0.85); border: 1px solid rgba(255, 255, 255, 0.1); padding: 40px 30px; border-radius: 28px; box-shadow: 0 30px 60px rgba(0,0,0,0.7); max-width: 380px; width: 90%; }" &
                   "  .icon-box { width: 80px; height: 80px; background: rgba(255, 82, 82, 0.1); border: 1px solid rgba(255, 82, 82, 0.3); border-radius: 22px; margin: 0 auto 20px; display: flex; align-items: center; justify-content: center; }" &
                   "  .icon-box svg { width: 40px; height: 40px; fill: #ff5252; }" &
                   "  h1 { font-size: 1.5rem; margin-bottom: 10px; }" &
                   "  p { font-size: 0.9rem; color: #8e95ab; margin-bottom: 25px; line-height: 1.4; }" &
                   "  .btn { background: linear-gradient(135deg, #00d2ff 0%, #0066ff 100%); color: #fff; border: none; padding: 14px 28px; border-radius: 14px; font-weight: 600; font-size: 0.95rem; cursor: pointer; transition: all 0.2s; width: 100%; }" &
                   "  .btn:hover { transform: translateY(-2px); filter: brightness(1.1); }" &
                   "</style>" &
                   "</head>" &
                   "<body>" &
                   "  <div class='card'>" &
                   "    <div class='icon-box'>" &
                   "      <svg viewBox='0 0 24 24'><path d='M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z'/></svg>" &
                   "    </div>" &
                   "    <h1>Server Offline</h1>" &
                   "    <p>Unable to connect to the launcher servers. Please check your internet connection and try again.</p>" &
                   "    <button class='btn' onclick='retry()'>Retry Connection</button>" &
                   "  </div>" &
                   "  <script>" &
                   "    function retry() { if(window.chrome && window.chrome.webview) window.chrome.webview.postMessage('retry_connection'); }" &
                   "  </script>" &
                   "</body>" &
                   "</html>"
        End Function

        Private Sub InitializeCustomComponents()
            Me.webView = New Microsoft.Web.WebView2.WinForms.WebView2()
            CType(Me.webView, System.ComponentModel.ISupportInitialize).BeginInit()
            Me.SuspendLayout()

            Me.BackColor = Color.FromArgb(15, 17, 26)

            Me.webView.Dock = DockStyle.Fill
            Me.webView.Location = New Point(0, 0)
            Me.webView.Name = "webView"
            Me.webView.TabIndex = 0

            Me.AutoScaleDimensions = New SizeF(8.0!, 16.0!)
            Me.AutoScaleMode = AutoScaleMode.Font
            Me.ClientSize = New Size(650, 750)
            Me.FormBorderStyle = FormBorderStyle.FixedSingle
            Me.MaximizeBox = False
            Me.MinimizeBox = True
            Me.StartPosition = FormStartPosition.CenterScreen

            Me.Controls.Add(Me.webView)
            Me.Name = "MainWindow"
            Me.Text = "Roblox Launcher"

            Dim iconPath As String = Path.Combine(Application.StartupPath, "roblox.ico")
            If Not File.Exists(iconPath) Then
                iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "roblox.ico")
            End If

            If File.Exists(iconPath) Then
                Try
                    Me.Icon = New Icon(iconPath)
                Catch ex As Exception
                    Debug.WriteLine($"Failed to load icon: {ex.Message}")
                End Try
            End If

            CType(Me.webView, System.ComponentModel.ISupportInitialize).EndInit()
            Me.ResumeLayout(False)
        End Sub

        Private Sub InitializeComponent()
            Me.SuspendLayout()

            Me.ClientSize = New System.Drawing.Size(282, 253)
            Me.Name = "MainWindow"
            Me.ResumeLayout(False)
        End Sub

    End Class
End Namespace