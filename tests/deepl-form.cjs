// Execute the actual embedded page script with a minimal DOM/API adapter.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync('Jellyfin.Plugin.MetaTube/Configuration/configPage.html', 'utf8');
const handlers = new Map(), fields = new Map();
let config = { DeepLApiUrl: 'https://old.invalid', Server: 'https://backend.invalid', Token: 'synthetic' };
const page = { querySelector: key => {
    if (!fields.has(key)) fields.set(key, {});
    return fields.get(key);
}};
function $(key) {
    const field = page.querySelector(key);
    const api = {
        on: (event, callback) => { handlers.set(key + ':' + event, callback); return api; },
        val: function(value) { if (arguments.length) { field.value = value; return api; } return field.value; },
        prop: (name) => field[name], change: () => api, css: () => api
    };
    return api;
}
vm.runInNewContext(html.match(/<script[^>]*>([\s\S]*?)<\/script>/i)[1], {
    $, Dashboard: { showLoadingMsg(){}, hideLoadingMsg(){}, processPluginConfigurationUpdateResult(){} },
    ApiClient: {
        getPluginConfiguration: async () => structuredClone(config),
        updatePluginConfiguration: async (_, value) => { config = structuredClone(value); }
    }
});
(async () => {
    for (const endpoint of ['https://deepl.example.invalid/custom', '']) {
        handlers.get('.MetaTubeConfigurationPage:pageshow').call(page);
        await new Promise(setImmediate);
        $('#txtDeepLApiUrl').val(endpoint);
        handlers.get('.MetaTubeConfigurationForm:submit').call(page);
        await new Promise(setImmediate);
        assert.equal(config.DeepLApiUrl, endpoint);
        assert.equal(config.Server, 'https://backend.invalid');
        assert.equal(config.Token, 'synthetic');
        $('#txtDeepLApiUrl').val('away');
        handlers.get('.MetaTubeConfigurationPage:pageshow').call(page);
        await new Promise(setImmediate);
        assert.equal($('#txtDeepLApiUrl').val(), endpoint);
    }
    console.log('DeepL form save/reload and clear checks passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
