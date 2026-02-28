extension AppDelegate: WKNavigationDelegate {

    // Handle file downloads (PDF/ZIP exports) — WKWebView doesn't download by default
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        
        if navigationAction.shouldPerformDownload {
            decisionHandler(.download)
            return
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
            
            // Trigger download for attachment responses
            decisionHandler(.download)
            return
        }

        decisionHandler(.allow)
    }
    
    // Attach download delegate to any downloads created
    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }
    
    func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        download.delegate = self
    }
}
