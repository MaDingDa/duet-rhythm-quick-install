import { createApplication } from '../server/node.mjs';

// The launcher owns this pipe. Closing the launcher also closes the server.
let server;
process.stdin.once('end', () => {
  if (!server?.listening) process.exit(0);
  server.close();
  server.closeAllConnections();
});
process.stdin.resume();

server = await createApplication({ DATA_DIR: process.env.DUET_DESKTOP_DATA });
server.on('error', error => {
  console.error(error.code ?? error.message);
  process.exitCode = 1;
  process.stdin.pause();
});
server.listen(43120, '127.0.0.1', () => console.log('READY'));
