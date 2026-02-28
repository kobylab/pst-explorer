import Cocoa
import WebKit
import Foundation

// ─────────────────────────────────────────────────────────────────────────────
// PST Explorer — Native macOS app with embedded WKWebView
//
//   1. Starts the .NET backend server as a child process
//   2. Opens a native window with WKWebView pointing at localhost
//   3. Handles downloads natively (save panel for PDF/ZIP exports)
//   4. Full menu bar: File > Reveal Data, Window controls, Quit
//   5. Closing the window quits the app and stops the server
// ─────────────────────────────────────────────────────────────────────────────

class AppDelegate: NSObject, NSApplicationDelegate, WKDownloadDelegate {

    var window: NSWindow!
    var webView: WKWebView!
    var serverProcess: Process?
    var port: Int = 9090

    // Paths
    let dataDir: String = {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return "\(home)/Documents/PST Explorer"
    }()

    var pstDir: String { "\(dataDir)/pst_files" }
    var exportDir: String { "\(dataDir)/exports" }
    var logFile: String { "\(dataDir)/pst-explorer.log" }
    var pidFile: String { "\(dataDir)/.server.pid" }
    var portFile: String { "\(dataDir)/.server.port" }

    var appDir: String {
        let bundle = Bundle.main.bundlePath
        return "\(bundle)/Contents/Resources/app"
    }

    // ── App launched ─────────────────────────────────────────────────────────

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Create data dirs
        let fm = FileManager.default
        try? fm.createDirectory(atPath: pstDir, withIntermediateDirectories: true)
        try? fm.createDirectory(atPath: exportDir, withIntermediateDirectories: true)

        setupMenuBar()
        createWindow()

        // Check if a server is already running
        if let existingPid = readPid(), isProcessRunning(existingPid) {
            if let savedPort = readPort() {
                port = savedPort
            }
            loadApp()
            return
        }

        // Find available port
        port = findAvailablePort(startingAt: 9090)
        guard port > 0 else {
            showAlert("Could not find an available port (9090-9200).")
            NSApp.terminate(nil)
            return
        }

        startServer()
    }

    // ── Window ───────────────────────────────────────────────────────────────

    func createWindow() {
        // WebView configuration
        let config = WKWebViewConfiguration()
        config.userContentController.add(self, name: "downloadBlob")

        webView = WKWebView(frame: .zero, configuration: config)
        webView.navigationDelegate = self
        webView.uiDelegate = self
        webView.setValue(false, forKey: "drawsBackground")  // transparent while loading

        // Show a loading message
        webView.loadHTMLString("""
            <html>
            <body style="background:#0d1117;color:#8b949e;font-family:-apple-system,sans-serif;
                         display:flex;align-items:center;justify-content:center;height:100vh;margin:0;">
              <div style="text-align:center">
                <div style="font-size:18px;font-weight:600;color:#c9d1d9;margin-bottom:8px;">PST Explorer</div>
                <div style="font-size:13px;">Starting server…</div>
              </div>
            </body>
            </html>
        """, baseURL: nil)

        // Window
        let screenRect = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let width: CGFloat = min(1440, screenRect.width * 0.85)
        let height: CGFloat = min(900, screenRect.height * 0.85)
        let x = screenRect.origin.x + (screenRect.width - width) / 2
        let y = screenRect.origin.y + (screenRect.height - height) / 2

        window = NSWindow(
            contentRect: NSRect(x: x, y: y, width: width, height: height),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "PST Explorer"
        
        // Add native vibrancy
        let visualEffectView = NSVisualEffectView(frame: window.contentRect(forFrameRect: window.frame))
        visualEffectView.material = .windowBackground
        visualEffectView.blendingMode = .behindWindow
        visualEffectView.state = .active
        visualEffectView.autoresizingMask = [.width, .height]
        
        webView.frame = visualEffectView.bounds
        webView.autoresizingMask = [.width, .height]
        visualEffectView.addSubview(webView)
        
        window.contentView = visualEffectView
        window.minSize = NSSize(width: 800, height: 500)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.makeKeyAndOrderFront(nil)
        window.titleVisibility = .hidden // Hide the native title since we have a custom one
        window.titlebarAppearsTransparent = true
        window.backgroundColor = .clear
        window.isOpaque = false

        NSApp.activate(ignoringOtherApps: true)
    }

    func loadApp() {
        let url = URL(string: "http://localhost:\(port)")!
        webView.load(URLRequest(url: url))
    }

    // ── Menu bar ─────────────────────────────────────────────────────────────

    func setupMenuBar() {
        let mainMenu = NSMenu()

        // App menu
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About PST Explorer", action: #selector(showAbout), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quit PST Explorer", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let appMenuItem = NSMenuItem()
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)

        // File menu
        let fileMenu = NSMenu(title: "File")
        fileMenu.addItem(withTitle: "Reveal Data in Finder", action: #selector(revealInFinder), keyEquivalent: "r")
        fileMenu.addItem(withTitle: "Reveal Exports in Finder", action: #selector(revealExportsInFinder), keyEquivalent: "e")
        fileMenu.addItem(.separator())
        fileMenu.addItem(withTitle: "Reload", action: #selector(reloadPage), keyEquivalent: "r")
        // Cmd+Shift+R for hard reload
        let hardReload = NSMenuItem(title: "Hard Reload", action: #selector(hardReloadPage), keyEquivalent: "r")
        hardReload.keyEquivalentModifierMask = [.command, .shift]
        fileMenu.addItem(hardReload)
        let fileMenuItem = NSMenuItem()
        fileMenuItem.submenu = fileMenu
        mainMenu.addItem(fileMenuItem)

        // Edit menu (for Cmd+C, Cmd+V, Cmd+A etc.)
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        let editMenuItem = NSMenuItem()
        editMenuItem.submenu = editMenu
        mainMenu.addItem(editMenuItem)

        // Window menu
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.miniaturize(_:)), keyEquivalent: "m")
        windowMenu.addItem(withTitle: "Zoom", action: #selector(NSWindow.zoom(_:)), keyEquivalent: "")
        windowMenu.addItem(.separator())
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.close), keyEquivalent: "w")
        let windowMenuItem = NSMenuItem()
        windowMenuItem.submenu = windowMenu
        mainMenu.addItem(windowMenuItem)

        NSApp.mainMenu = mainMenu
    }

    @objc func showAbout() {
        let alert = NSAlert()
        alert.messageText = "PST Explorer"
        alert.informativeText = "Browse, search, and export emails from Outlook PST/OST files.\n\nData: ~/Documents/PST Explorer/"
        alert.alertStyle = .informational
        alert.addButton(withTitle: "OK")
        alert.runModal()
    }

    @objc func revealInFinder() {
        NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: pstDir)
    }

    @objc func revealExportsInFinder() {
        NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: exportDir)
    }

    @objc func reloadPage() {
        webView.reload()
    }

    @objc func hardReloadPage() {
        webView.reloadFromOrigin()
    }

    // ── Quit ─────────────────────────────────────────────────────────────────

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        stopServer()
        return .terminateNow
    }

    // Re-open (user clicks Dock icon while app is running but window closed)
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if !hasVisibleWindows {
            window.makeKeyAndOrderFront(nil)
        }
        return false
    }

    // ── Server lifecycle ─────────────────────────────────────────────────────

    func startServer() {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "\(appDir)/PstWeb")
        process.currentDirectoryURL = URL(fileURLWithPath: appDir)
        process.environment = [
            "PST_DIR": pstDir,
            "EXPORT_DIR": exportDir,
            "PORT": "\(port)",
            "ASPNETCORE_ENVIRONMENT": "Production",
            "HOME": FileManager.default.homeDirectoryForCurrentUser.path,
            "PATH": "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        ]

        // Redirect stdout/stderr to log file
        let logHandle = FileHandle(forWritingAtPath: logFile) ?? {
            FileManager.default.createFile(atPath: logFile, contents: nil)
            return FileHandle(forWritingAtPath: logFile)!
        }()
        logHandle.seekToEndOfFile()
        process.standardOutput = logHandle
        process.standardError = logHandle

        process.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async {
                self?.cleanupPid()
                self?.serverProcess = nil
            }
        }

        do {
            try process.run()
            serverProcess = process
            writePid(process.processIdentifier)
            writePort(port)

            waitForServerReady {
                self.loadApp()
            }
        } catch {
            showAlert("Failed to start server: \(error.localizedDescription)\n\nCheck log at:\n\(logFile)")
            NSApp.terminate(nil)
        }
    }

    func stopServer() {
        if let proc = serverProcess, proc.isRunning {
            proc.terminate()
            proc.waitUntilExit()
        }
        serverProcess = nil

        if let pid = readPid(), isProcessRunning(pid) {
            kill(pid, SIGTERM)
        }

        cleanupPid()
    }

    // ── Readiness polling ────────────────────────────────────────────────────

    func waitForServerReady(attempts: Int = 0, completion: @escaping () -> Void) {
        if attempts > 60 {
            showAlert("Server failed to start within 30 seconds.\n\nCheck log at:\n\(logFile)")
            NSApp.terminate(nil)
            return
        }

        if let proc = serverProcess, !proc.isRunning {
            showAlert("Server crashed on startup.\n\nCheck log at:\n\(logFile)")
            NSApp.terminate(nil)
            return
        }

        let url = URL(string: "http://localhost:\(port)/api/pst/status")!
        let task = URLSession.shared.dataTask(with: url) { data, response, error in
            if let http = response as? HTTPURLResponse, http.statusCode == 200 {
                DispatchQueue.main.async { completion() }
            } else {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    self.waitForServerReady(attempts: attempts + 1, completion: completion)
                }
            }
        }
        task.resume()
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    func findAvailablePort(startingAt start: Int) -> Int {
        for p in start...9200 {
            if !isPortInUse(p) { return p }
        }
        return -1
    }

    func isPortInUse(_ port: Int) -> Bool {
        let sock = socket(AF_INET, SOCK_STREAM, 0)
        guard sock >= 0 else { return false }
        defer { close(sock) }

        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = in_port_t(port).bigEndian
        addr.sin_addr.s_addr = inet_addr("127.0.0.1")

        let result = withUnsafePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                connect(sock, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        return result == 0
    }

    func isProcessRunning(_ pid: Int32) -> Bool {
        kill(pid, 0) == 0
    }

    func writePid(_ pid: Int32) {
        try? "\(pid)".write(toFile: pidFile, atomically: true, encoding: .utf8)
    }

    func readPid() -> Int32? {
        guard let str = try? String(contentsOfFile: pidFile, encoding: .utf8)
                .trimmingCharacters(in: .whitespacesAndNewlines),
              let pid = Int32(str) else { return nil }
        return pid
    }

    func writePort(_ port: Int) {
        try? "\(port)".write(toFile: portFile, atomically: true, encoding: .utf8)
    }

    func readPort() -> Int? {
        guard let str = try? String(contentsOfFile: portFile, encoding: .utf8)
                .trimmingCharacters(in: .whitespacesAndNewlines),
              let p = Int(str) else { return nil }
        return p
    }

    func cleanupPid() {
        try? FileManager.default.removeItem(atPath: pidFile)
        try? FileManager.default.removeItem(atPath: portFile)
    }

    func showAlert(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "PST Explorer"
        alert.informativeText = message
        alert.alertStyle = .critical
        alert.addButton(withTitle: "OK")
        alert.runModal()
    }
}

// ── Window delegate ──────────────────────────────────────────────────────────

extension AppDelegate: NSWindowDelegate {
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        NSApp.terminate(nil)
        return false
    }
}

// ── WKWebView navigation delegate ────────────────────────────────────────────

extension AppDelegate: WKNavigationDelegate {

    // Handle navigation to blob: URLs (frontend creates blob downloads)
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        
        if #available(macOS 11.3, *) {
            if navigationAction.shouldPerformDownload {
                decisionHandler(.download)
                return
            }
        }

        if let url = navigationAction.request.url {
            // Block navigation away from localhost, but allow blob URLs
            if url.scheme == "blob" || (url.scheme == "http" && url.host == "localhost") {
                decisionHandler(.allow)
                return
            }
            // Open external links in the real browser
            if url.scheme == "https" || url.scheme == "http" {
                NSWorkspace.shared.open(url)
                decisionHandler(.cancel)
                return
            }
        }
        decisionHandler(.allow)
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationResponse: WKNavigationResponse,
                 decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {

        if let response = navigationResponse.response as? HTTPURLResponse,
           let contentDisp = response.value(forHTTPHeaderField: "Content-Disposition"),
           contentDisp.contains("attachment") || contentDisp.contains("filename") {
            
            if #available(macOS 11.3, *) {
                // Trigger download for attachment responses
                decisionHandler(.download)
                return
            }
        }

        decisionHandler(.allow)
    }
    
    @available(macOS 11.3, *)
    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }
    
    @available(macOS 11.3, *)
    func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        download.delegate = self
    }
}

// ── WKWebView download delegate ──────────────────────────────────────────────

extension AppDelegate {
    @available(macOS 11.3, *)
    func download(_ download: WKDownload, decideDestinationUsing response: URLResponse, suggestedFilename: String, completionHandler: @escaping (URL?) -> Void) {
        DispatchQueue.main.async {
            let savePanel = NSSavePanel()
            savePanel.nameFieldStringValue = suggestedFilename
            savePanel.canCreateDirectories = true

            if savePanel.runModal() == .OK, let dest = savePanel.url {
                completionHandler(dest)
            } else {
                completionHandler(nil)
            }
        }
    }
    
    @available(macOS 11.3, *)
    func download(_ download: WKDownload, didFailWithError error: Error, resumeData: Data?) {
        DispatchQueue.main.async {
            let alert = NSAlert()
            alert.messageText = "Download Failed"
            alert.informativeText = error.localizedDescription
            alert.addButton(withTitle: "OK")
            alert.runModal()
        }
    }
}

// ── WKWebView UI delegate (handles JS confirm/alert, new window requests) ────

extension AppDelegate: WKUIDelegate {

    // Handle window.open() and target="_blank" links — open in same webview
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = navigationAction.request.url {
            webView.load(URLRequest(url: url))
        }
        return nil
    }

    // Handle JavaScript alert()
    func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        let alert = NSAlert()
        alert.messageText = "PST Explorer"
        alert.informativeText = message
        alert.addButton(withTitle: "OK")
        alert.runModal()
        completionHandler()
    }

    // Handle JavaScript confirm()
    func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        let alert = NSAlert()
        alert.messageText = "PST Explorer"
        alert.informativeText = message
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Cancel")
        completionHandler(alert.runModal() == .alertFirstButtonReturn)
    }

    // Handle file input (<input type="file">)
    func webView(_ webView: WKWebView, runOpenPanelWith parameters: WKOpenPanelParameters,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping ([URL]?) -> Void) {
        
        let openPanel = NSOpenPanel()
        openPanel.canChooseFiles = true
        openPanel.canChooseDirectories = parameters.allowsDirectories
        openPanel.allowsMultipleSelection = parameters.allowsMultipleSelection
        
        if openPanel.runModal() == .OK {
            completionHandler(openPanel.urls)
        } else {
            completionHandler(nil)
        }
}
}

// ── Entry point ──────────────────────────────────────────────────────────────

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = AppDelegate()
app.delegate = delegate
app.run()

// ── WKScriptMessageHandler ───────────────────────────────────────────────────

extension AppDelegate: WKScriptMessageHandler {
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        if message.name == "downloadBlob",
           let dict = message.body as? [String: Any],
           let base64String = dict["base64"] as? String,
           let filename = dict["filename"] as? String {
            
            let components = base64String.components(separatedBy: ",")
            if components.count > 1, let data = Data(base64Encoded: components[1]) {
                
                let savePanel = NSSavePanel()
                savePanel.nameFieldStringValue = filename
                savePanel.canCreateDirectories = true
                if savePanel.runModal() == .OK, let url = savePanel.url {
                    do {
                        try data.write(to: url)
                    } catch {
                        showAlert("Failed to save file: \(error.localizedDescription)")
                    }
                }
            }
        }
    }
}
