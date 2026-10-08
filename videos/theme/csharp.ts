import {LezerHighlighter} from '@revideo/2d';
import {csharpLanguage} from '@replit/codemirror-lang-csharp';

export const csharp = new LezerHighlighter(csharpLanguage.parser);
