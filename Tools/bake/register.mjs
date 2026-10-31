// node --import ./Tools/bake/register.mjs <script> — installs hooks.mjs (three.js import map)
import { register } from 'node:module';
register('./hooks.mjs', import.meta.url);
