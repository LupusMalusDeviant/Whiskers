window.ServerWatch = window.ServerWatch || {};

window.ServerWatch.Graph = {
    instance: null,

    create: function (elementId, nodesData, edgesData) {
        var container = document.getElementById(elementId);
        if (!container) return;

        var nodes = new vis.DataSet(nodesData.map(function (n) {
            var colors = {
                running: { background: 'rgba(52, 211, 153, 0.15)', border: '#34d399', font: '#34d399' },
                exited: { background: 'rgba(248, 113, 113, 0.15)', border: '#f87171', font: '#f87171' },
                database: { background: 'rgba(96, 165, 250, 0.15)', border: '#60a5fa', font: '#60a5fa' },
                default: { background: 'rgba(255, 255, 255, 0.05)', border: '#555', font: '#aaa' }
            };
            var c = n.isDatabase ? colors.database : (colors[n.state] || colors.default);
            return {
                id: n.id,
                label: n.label,
                title: n.tooltip,
                group: n.group,
                shape: n.isDatabase ? 'database' : 'box',
                color: { background: c.background, border: c.border, highlight: { background: c.background, border: '#fff' } },
                font: { color: c.font, size: 12, face: 'Inter, sans-serif' },
                borderWidth: 1.5,
                margin: 10
            };
        }));

        var edges = new vis.DataSet(edgesData.map(function (e) {
            return {
                from: e.from,
                to: e.to,
                label: e.label || '',
                color: { color: 'rgba(255,255,255,0.12)', highlight: 'rgba(255,255,255,0.3)' },
                font: { color: '#666', size: 9, strokeWidth: 0 },
                arrows: e.arrows ? 'to' : '',
                dashes: e.dashes || false,
                smooth: { type: 'cubicBezier', roundness: 0.4 }
            };
        }));

        var options = {
            physics: {
                solver: 'forceAtlas2Based',
                forceAtlas2Based: { gravitationalConstant: -60, centralGravity: 0.005, springLength: 150, springConstant: 0.02 },
                stabilization: { iterations: 100 }
            },
            layout: { improvedLayout: true },
            interaction: { hover: true, tooltipDelay: 200, zoomView: true, dragView: true },
            nodes: { borderWidthSelected: 2 },
            edges: { width: 1 }
        };

        this.instance = new vis.Network(container, { nodes: nodes, edges: edges }, options);
    },

    destroy: function () {
        if (this.instance) {
            this.instance.destroy();
            this.instance = null;
        }
    }
};
