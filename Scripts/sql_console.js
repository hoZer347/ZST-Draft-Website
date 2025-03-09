(async function () {
    const SQL = await initSqlJs({
        locateFile: file => `https://cdnjs.cloudflare.com/ajax/libs/sql.js/1.8.0/sql-wasm.wasm`
    });

    const consoleContainer = document.createElement('div');
    consoleContainer.id = 'sql-console';
    consoleContainer.style = `
        background-color: #1e1e1e;
        color: #dcdcdc;
        font-family: monospace;
        padding: 10px;
        border: 2px solid #3a3a3a;
        border-radius: 8px;
        margin: 20px 0;
        width: 100%;
        max-width: 600px;
    `;

    const sqlInput = document.createElement('textarea');
    sqlInput.placeholder = 'Enter SQL query here...';
    sqlInput.rows = 5;
    sqlInput.style = 'width: 100%; margin-bottom: 10px;';

    const runButton = document.createElement('button');
    runButton.textContent = 'Run SQL';
    runButton.style = 'background-color: #0078d4; color: #fff; border: none; padding: 5px 10px; cursor: pointer;';

    const clearButton = document.createElement('button');
    clearButton.textContent = 'Clear Data';
    clearButton.style = 'background-color: #f44336; color: #fff; border: none; padding: 5px 10px; cursor: pointer; margin-left: 10px;';

    const outputDiv = document.createElement('div');
    outputDiv.id = 'sql-output';
    outputDiv.style = 'background-color: #252526; padding: 5px; border-radius: 4px; margin-top: 10px;';

    consoleContainer.appendChild(sqlInput);
    consoleContainer.appendChild(runButton);
    consoleContainer.appendChild(clearButton);
    consoleContainer.appendChild(outputDiv);

    document.body.appendChild(consoleContainer);

    let db;
    try {
        const response = await fetch('../database.db');
        if (!response.ok) throw new Error('Failed to load database file.');
        const buffer = await response.arrayBuffer();
        db = new SQL.Database(new Uint8Array(buffer));
    } catch (error) {
        outputDiv.innerHTML = `<p style="color: #f44336;">Error loading database: ${error.message}</p>`;
        return;
    }

    runButton.addEventListener('click', () => {
        const query = sqlInput.value.trim();
        if (!query) {
            outputDiv.textContent = 'Please enter a valid SQL query.';
            return;
        }

        try {
            const result = db.exec(query);

            if (result.length === 0) {
                outputDiv.innerHTML = `<p>No results found.</p>`;
            } else {
                const htmlTable = result.map(table => {
                    const headers = `<tr>${table.columns.map(col => `<th>${col}</th>`).join('')}</tr>`;
                    const rows = table.values.map(row =>
                        `<tr>${row.map(val => `<td>${val}</td>`).join('')}</tr>`
                    ).join('');
                    return `<table style="width:100%;border-collapse:collapse;border:1px solid #3a3a3a;">
                                ${headers}${rows}
                            </table>`;
                }).join('');

                outputDiv.innerHTML = htmlTable;
            }
        } catch (error) {
            outputDiv.innerHTML = `<p style="color: #f44336;">SQL Error: ${error.message}</p>`;
        }
    });

    clearButton.addEventListener('click', () => {
        sqlInput.value = '';
        outputDiv.innerHTML = '';
    });
})();
