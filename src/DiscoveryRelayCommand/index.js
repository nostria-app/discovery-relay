#!/usr/bin/env node

const { program } = require('commander');
const lmdb = require('node-lmdb');
const chalk = require('chalk');
const path = require('path');
const { table } = require('table');
const fs = require('fs');

// Setup CLI program
program
  .name('discovery-relay')
  .description('CLI tool to interact with Discovery Relay LMDB database')
  .version('1.0.0');

// Command to retrieve events from LMDB
program
  .option('-p, --path <path>', 'Path to LMDB database', '../DiscoveryRelay/data')
  .option('-e, --events <number>', 'Number of events to return', '10')
  .option('-k, --kind <number>', 'Filter by event kind (3 or 10002)', '')
  .option('-f, --format <format>', 'Output format (table, json)', 'table')
  .option('-v, --verbose', 'Show detailed event information');

program.parse(process.argv);
const options = program.opts();

// Verify database path exists
const dbPath = path.resolve(__dirname, options.path);
if (!fs.existsSync(dbPath)) {
  console.error(chalk.red(`Database path not found: ${dbPath}`));
  console.log(chalk.yellow('Make sure the path to the LMDB database is correct.'));
  process.exit(1);
}

// Setup LMDB environment
const env = new lmdb.Env();
try {
  env.open({
    path: dbPath,
    maxDbs: 10
  });

  // Open database
  const db = env.openDbi({
    name: 'events',
    create: false
  });

  // Convert string options to numbers
  const limit = parseInt(options.events, 10);
  const kindFilter = options.kind ? parseInt(options.kind, 10) : null;

  // Validate kind filter if provided
  if (kindFilter !== null && kindFilter !== 3 && kindFilter !== 10002) {
    console.error(chalk.red('Kind filter must be either 3 or 10002'));
    env.close();
    process.exit(1);
  }

  // Query events
  const txn = env.beginTransaction({ readOnly: true });
  const cursor = new lmdb.Cursor(txn, db);

  const events = [];
  let count = 0;

  // Iterate through events
  if (cursor.goToFirst()) {
    do {
      const key = cursor.getCurrentString('utf8');
      const value = cursor.getCurrentBinary();

      try {
        const event = JSON.parse(value.toString('utf8'));

        // Apply kind filter if needed
        if (kindFilter !== null && event.kind !== kindFilter) {
          continue;
        }

        events.push({
          key,
          event
        });

        count++;
        if (count >= limit) {
          break;
        }
      } catch (err) {
        console.error(chalk.red(`Failed to parse event: ${err.message}`));
      }
    } while (cursor.goToNext());
  }

  // Cleanup
  cursor.close();
  txn.abort();

  // Output results
  if (events.length === 0) {
    console.log(chalk.yellow('No events found matching the criteria.'));
  } else {
    if (options.format === 'json') {
      console.log(JSON.stringify(events, null, 2));
    } else {
      // Table format
      const tableData = [
        [
          chalk.bold('ID'),
          chalk.bold('Kind'),
          chalk.bold('Pubkey'),
          chalk.bold('Created At'),
          chalk.bold('Content')
        ]
      ];

      events.forEach(({ event }) => {
        const createdDate = new Date(event.created_at * 1000).toISOString();
        const truncatedContent = event.content.length > 30 ?
          `${event.content.substring(0, 27)}...` :
          event.content;
        const truncatedPubkey = `${event.pubkey.substring(0, 8)}...`;

        tableData.push([
          event.id.substring(0, 8) + '...',
          event.kind,
          truncatedPubkey,
          createdDate,
          truncatedContent
        ]);
      });

      console.log(table(tableData));
      console.log(chalk.green(`Showing ${events.length} events of ${count} total`));

      // Show verbose output if requested
      if (options.verbose) {
        events.forEach(({ event }, index) => {
          console.log(chalk.bold(`\n--- Event ${index + 1} ---`));
          console.log(`ID: ${event.id}`);
          console.log(`Pubkey: ${event.pubkey}`);
          console.log(`Kind: ${event.kind}`);
          console.log(`Created: ${new Date(event.created_at * 1000).toISOString()}`);
          console.log(`Tags: ${JSON.stringify(event.tags)}`);
          console.log(`Content: ${event.content}`);
        });
      }
    }
  }
} catch (err) {
  console.error(chalk.red(`Error: ${err.message}`));
} finally {
  env.close();
}