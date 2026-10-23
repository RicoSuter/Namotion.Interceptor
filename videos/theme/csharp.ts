import {HighlightStyle} from '@codemirror/language';
import {tags} from '@lezer/highlight';
import {LezerHighlighter} from '@revideo/2d';
import {csharpLanguage} from '@replit/codemirror-lang-csharp';
import {syntax} from './palette';

const style = HighlightStyle.define([
  {tag: [tags.keyword, tags.bool, tags.null], color: syntax.keyword},
  {tag: [tags.typeName, tags.className], color: syntax.type},
  {tag: tags.propertyName, color: syntax.property},
  {tag: tags.function(tags.variableName), color: syntax.method},
  {tag: [tags.string, tags.character], color: syntax.string},
  {tag: [tags.number, tags.integer, tags.float], color: syntax.number},
  {tag: tags.comment, color: syntax.comment},
  {tag: [tags.operator, tags.paren, tags.brace, tags.squareBracket], color: syntax.punctuation},
]);

export const csharp = new LezerHighlighter(csharpLanguage.parser, style);
