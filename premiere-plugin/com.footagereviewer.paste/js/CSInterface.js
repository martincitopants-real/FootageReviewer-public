/*
 * Minimal stand-in for Adobe's CSInterface.js — just the two calls this panel needs, wrapping the
 * window.__adobe_cep__ bridge that CEP injects. Keeping it small (rather than vendoring Adobe's full
 * library) means there's nothing here that isn't used or reviewable.
 */
var SystemPath = { USER_DATA: 'userData', EXTENSION: 'extension', COMMON_FILES: 'commonFiles' };

function CSInterface() { }

CSInterface.prototype.evalScript = function (script, callback) {
    if (typeof window.__adobe_cep__ === 'undefined') {
        if (callback) callback('ERR|Not running inside Premiere (CEP bridge missing).');
        return;
    }
    window.__adobe_cep__.evalScript(script, callback || function () { });
};

CSInterface.prototype.getSystemPath = function (type) {
    if (typeof window.__adobe_cep__ === 'undefined') return '';
    var p = window.__adobe_cep__.getSystemPath(type);
    // CEP hands back a file:// URL on some hosts; normalise to a plain path with forward slashes.
    p = decodeURIComponent(String(p || '')).replace(/^file:\/{2,}/, '').replace(/\\/g, '/');
    return p;
};

CSInterface.prototype.getHostEnvironment = function () {
    if (typeof window.__adobe_cep__ === 'undefined') return {};
    try { return JSON.parse(window.__adobe_cep__.getHostEnvironment()); } catch (e) { return {}; }
};
